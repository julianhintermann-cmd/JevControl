using System.Text;
using System.Text.Json.Nodes;
using Kairo.Core.Abstractions;
using Kairo.Core.AI.Jev;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Security;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Agent;

public sealed record StepVerification(ResolvedStep Step, bool Verified, string? Observed, string? Problem);

/// <summary>
/// Verifies executed steps with targeted read-back (only the touched elements, no full re-read) and –
/// for the overall goal – a single Jev yes/no question on the final state.
/// </summary>
public sealed class Verifier
{
    private readonly PerceptionService _perception;
    private readonly IDecisionModel? _jev;
    private readonly KairoLogger _log;

    public Verifier(PerceptionService perception, IDecisionModel? jev, KairoLogger log)
    {
        _perception = perception;
        _jev = jev;
        _log = log;
    }

    public async Task<IReadOnlyList<StepVerification>> VerifyValuesAsync(IReadOnlyList<ResolvedStep> executed, UiSnapshot snapshot, SecretVault vault, CancellationToken cancellationToken)
    {
        var checkable = executed
            .Where(s => s.Element is not null && s.Action.Kind is ActionKind.SetValue or ActionKind.SetChecked or ActionKind.SelectOption && !s.Element.IsPassword)
            .ToList();
        if (checkable.Count == 0) { return []; }

        IReadOnlyDictionary<string, ElementState> states;
        try
        {
            states = await _perception.ReadStatesAsync(snapshot, checkable.Select(s => s.Element!).DistinctBy(e => e.Locator).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("verify", $"read-back failed: {ex.GetType().Name}");
            return checkable.Select(s => new StepVerification(s, false, null, "Wert konnte nicht zurückgelesen werden.")).ToList();
        }

        var results = new List<StepVerification>();
        foreach (var step in checkable)
        {
            if (!states.TryGetValue(step.Element!.Locator, out var state) || !state.Exists)
            {
                results.Add(new StepVerification(step, false, null, "Element nicht mehr vorhanden."));
                continue;
            }

            switch (step.Action.Kind)
            {
                case ActionKind.SetValue:
                {
                    var expected = vault.Unmask(step.Action.Value ?? "");
                    var ok = ValuesMatch(expected, state.Value);
                    results.Add(new StepVerification(step, ok, vault.Mask(state.Value), ok ? null : $"Feld „{step.Element.DisplayLabel}“ enthält nicht den erwarteten Wert."));
                    break;
                }
                case ActionKind.SetChecked:
                {
                    var wanted = step.Action.Checked ?? true;
                    var ok = state.IsChecked == wanted;
                    results.Add(new StepVerification(step, ok, state.IsChecked?.ToString(), ok ? null : $"„{step.Element.DisplayLabel}“ ist nicht {(wanted ? "aktiviert" : "deaktiviert")}."));
                    break;
                }
                case ActionKind.SelectOption:
                {
                    var wanted = step.Action.Option ?? step.Action.Value ?? "";
                    var observed = state.SelectedOption ?? state.Value;
                    var ok = ValuesMatch(wanted, observed) || (observed?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false);
                    results.Add(new StepVerification(step, ok, observed, ok ? null : $"In „{step.Element.DisplayLabel}“ ist nicht „{wanted}“ ausgewählt."));
                    break;
                }
            }
        }
        return results;
    }

    /// <summary>Tolerant comparison: whitespace, case of e-mails, phone formatting, line endings.</summary>
    public static bool ValuesMatch(string? expected, string? observed)
    {
        var e = Normalize(expected);
        var o = Normalize(observed);
        if (e == o) { return true; }
        if (e.Length == 0) { return o.Length == 0; }
        if (string.Equals(e, o, StringComparison.OrdinalIgnoreCase)) { return true; }

        var ed = new string(e.Where(char.IsDigit).ToArray());
        var od = new string(o.Where(char.IsDigit).ToArray());
        var mostlyDigits = ed.Length >= 5 && ed.Length >= e.Count(char.IsLetterOrDigit) * 0.8;
        if (mostlyDigits && ed == od) { return true; }

        // Input masks may add formatting characters but must keep the characters in order.
        var ec = new string(e.Where(char.IsLetterOrDigit).ToArray());
        var oc = new string(o.Where(char.IsLetterOrDigit).ToArray());
        return ec.Length > 0 && string.Equals(ec, oc, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return ""; }
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Replace("\r\n", "\n").Trim())
        {
            if (char.IsWhiteSpace(c) && c != '\n')
            {
                if (!space) { sb.Append(' '); }
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Jev noul: "has the user's goal been achieved?" on the final UI state and the list of executed steps.
    /// Returns null when Jev is not available.
    /// </summary>
    public async Task<double?> CheckGoalAsync(string instruction, IReadOnlyList<string> executedSteps, UiSnapshot? snapshot, string model, SecretVault vault, CancellationToken cancellationToken)
    {
        if (_jev is null) { return null; }
        try
        {
            var state = new JsonObject
            {
                ["user_goal"] = SnapshotFormatter.Clip(instruction, 500),
                ["executed_steps"] = new JsonArray(executedSteps.TakeLast(40).Select(s => (JsonNode)JsonValue.Create(SnapshotFormatter.Clip(s, 160))!).ToArray()),
            };
            if (snapshot is not null)
            {
                state["application"] = snapshot.Window.ProcessName;
                state["window_title"] = SnapshotFormatter.Clip(snapshot.Window.Title, 120);
                state["current_ui"] = new JsonArray(snapshot.Elements.Take(80).Select(e => (JsonNode)JsonValue.Create(SnapshotFormatter.FormatElement(e, vault, 60))!).ToArray());
                if (snapshot.TextBlocks.Count > 0)
                {
                    state["visible_texts"] = new JsonArray(snapshot.TextBlocks.Take(15).Select(t => (JsonNode)JsonValue.Create(SnapshotFormatter.Clip(vault.Mask(t), 140))!).ToArray());
                }
            }

            var response = await _jev.DecideAsync(new JevRequest
            {
                Model = model,
                Purpose = "verify",
                State = state,
                Questions = new Dictionary<string, JevQuestion>
                {
                    ["goal_achieved"] = JevQuestion.Noul(
                        "Based on current_ui, visible_texts and executed_steps: has user_goal been fully achieved? Only what the user explicitly asked for counts (e.g. a form that should only be filled in does not need to be submitted).",
                        "Everything the user asked for is visibly done",
                        "Something the user asked for is missing, wrong or shows an error"),
                },
            }, cancellationToken).ConfigureAwait(false);
            return response["goal_achieved"]?.Noul;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("verify", $"Jev goal check unavailable: {ex.GetType().Name}");
            return null;
        }
    }
}
