using System.Globalization;
using System.Text.Json.Nodes;
using Kairo.Core.AI.Jev;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Security;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Agent;

public enum ResolutionSource
{
    /// <summary>Only one compatible element and it matches the plan – no model call needed.</summary>
    Unambiguous,
    /// <summary>Jev and the planner agree.</summary>
    JevAgreesWithPlanner,
    /// <summary>Jev picked a different element with high confidence.</summary>
    JevOverride,
    /// <summary>Jev picked the element (the planner gave no usable id).</summary>
    JevChoice,
    /// <summary>Jev was uncertain; the planner's element was plausible and kept.</summary>
    PlannerPreferred,
    /// <summary>Jev unavailable – planner hint used.</summary>
    PlannerFallback,
    /// <summary>Jev unavailable – best local lexical match used.</summary>
    LocalMatch,
    /// <summary>Jev decided the target is not visible – scroll first.</summary>
    NeedsScroll,
    Unresolved,
}

/// <summary>An element step mapped onto a concrete element of the current snapshot.</summary>
public sealed record ResolvedStep
{
    public required AgentAction Action { get; init; }
    public UiElement? Element { get; init; }
    public required ResolutionSource Source { get; init; }
    public double Probability { get; init; }
    public double? Confidence { get; init; }
    public string? Problem { get; init; }
    /// <summary>The discrete options Jev chose from (criterion key → description), for transparency.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    public bool IsResolved => Element is not null && Source is not (ResolutionSource.Unresolved or ResolutionSource.NeedsScroll);
}

/// <summary>
/// Uses Jev (TypeSafe System One model) to pick, for each planned element step, the concrete action among the
/// actions Kairo determined to be valid in the current UI state (A: focus field X, B: field Y, …, scroll, none).
/// All steps of a batch are asked in ONE Decisions API request (questions are evaluated in parallel).
/// Only elements that were validated as compatible can ever be chosen.
/// </summary>
public sealed class TargetResolver
{
    public const string NoneKey = "none";
    public const string ScrollKey = "scroll";
    private const int MaxCandidates = 8;

    private readonly IDecisionModel? _jev;
    private readonly KairoLogger _log;

    public TargetResolver(IDecisionModel? jev, KairoLogger log)
    {
        _jev = jev;
        _log = log;
    }

    public async Task<IReadOnlyList<ResolvedStep>> ResolveAsync(
        IReadOnlyList<AgentAction> steps,
        UiSnapshot snapshot,
        string instruction,
        string jevModel,
        bool useJev,
        double actThreshold,
        SecretVault? vault,
        CancellationToken cancellationToken)
    {
        var results = new ResolvedStep?[steps.Count];
        var pending = new List<(int Index, AgentAction Step, IReadOnlyList<(UiElement Element, double Score)> Candidates, UiElement? Hint)>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var candidates = ElementMatcher.RankCandidates(step, snapshot, MaxCandidates);
            var hint = step.TargetId is { } id ? snapshot.Find(id) : null;
            if (hint is not null && !ElementMatcher.IsCompatible(step.Kind, hint)) { hint = null; }
            // A hinted id whose label clearly differs from the planned label is not trusted.
            if (hint is not null && !string.IsNullOrWhiteSpace(step.TargetLabel) && ElementMatcher.Similarity(step.TargetLabel, hint) < 0.35 &&
                candidates.Any(c => c.Element.Id != hint.Id && ElementMatcher.Similarity(step.TargetLabel, c.Element) >= 0.8))
            {
                hint = null;
            }

            if (candidates.Count == 0)
            {
                results[i] = new ResolvedStep
                {
                    Action = step,
                    Source = snapshot.Truncated || snapshot.Elements.Any(e => e.IsOffscreen) ? ResolutionSource.NeedsScroll : ResolutionSource.Unresolved,
                    Problem = $"Kein passendes Element für „{step.TargetLabel ?? step.Description}“ gefunden.",
                };
                continue;
            }

            if (candidates.Count == 1 && (hint is null || hint.Id == candidates[0].Element.Id) &&
                (hint is not null || candidates[0].Score >= 0.75))
            {
                results[i] = new ResolvedStep { Action = step, Element = candidates[0].Element, Source = ResolutionSource.Unambiguous, Probability = 1 };
                continue;
            }

            pending.Add((i, step, candidates, hint));
        }

        if (pending.Count > 0)
        {
            JevResponse? response = null;
            Dictionary<string, Dictionary<string, string>>? optionsByQuestion = null;
            if (useJev && _jev is not null)
            {
                try
                {
                    (response, optionsByQuestion) = await AskJevAsync(pending, snapshot, instruction, jevModel, vault, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("resolver", $"Jev unavailable, using planner hints: {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var p in pending)
            {
                var key = QuestionKey(p.Index);
                var answer = response?[key];
                results[p.Index] = answer is null
                    ? Fallback(p.Step, p.Candidates, p.Hint)
                    : Decide(p.Step, p.Candidates, p.Hint, answer, actThreshold, optionsByQuestion?[key]);
            }
        }

        var list = results.Select(r => r!).ToList();
        ResolveConflicts(list);
        return list;
    }

    private static string QuestionKey(int index) => "s" + index.ToString(CultureInfo.InvariantCulture);

    private static string ElementKey(int id) => "e" + id.ToString(CultureInfo.InvariantCulture);

    private async Task<(JevResponse Response, Dictionary<string, Dictionary<string, string>> Options)> AskJevAsync(
        List<(int Index, AgentAction Step, IReadOnlyList<(UiElement Element, double Score)> Candidates, UiElement? Hint)> pending,
        UiSnapshot snapshot,
        string instruction,
        string model,
        SecretVault? vault,
        CancellationToken cancellationToken)
    {
        var questions = new Dictionary<string, JevQuestion>();
        var optionsByQuestion = new Dictionary<string, Dictionary<string, string>>();
        var offscreenOrTruncated = snapshot.Truncated || snapshot.Elements.Any(e => e.IsOffscreen);

        foreach (var (index, step, candidates, _) in pending)
        {
            var criteria = new Dictionary<string, JevCriterion>();
            var options = new Dictionary<string, string>();
            foreach (var (element, _) in candidates)
            {
                var description = DescribeCandidate(step, element, vault);
                criteria[ElementKey(element.Id)] = new JevCriterion(description);
                options[ElementKey(element.Id)] = description;
            }
            if (offscreenOrTruncated)
            {
                criteria[ScrollKey] = new JevCriterion("Scroll first: the right control is not among the listed ones and is probably further down the page");
                options[ScrollKey] = "Scrollen";
            }
            criteria[NoneKey] = new JevCriterion("None of the listed controls fits this step – report the step as blocked");
            options[NoneKey] = "Blockiert melden";

            var what = step.Kind switch
            {
                ActionKind.SetValue => $"enter {DescribeValue(step.Value, vault)}",
                ActionKind.SelectOption => $"choose \"{step.Option ?? step.Value}\"",
                ActionKind.SetChecked => step.Checked == false ? "uncheck the option" : "check the option",
                ActionKind.Focus => "focus the control",
                _ => "activate the control",
            };
            var instructions =
                $"Current step: \"{step.Description}\" — the user wants to {what} in the control labelled \"{step.TargetLabel}\". " +
                "Which of the valid actions should Kairo perform next to achieve exactly this step?";

            var key = QuestionKey(index);
            questions[key] = JevQuestion.Choice(instructions, criteria);
            optionsByQuestion[key] = options;
        }

        var state = new JsonObject
        {
            ["user_goal"] = SnapshotFormatter.Clip(instruction, 400),
            ["application"] = snapshot.Window.ProcessName,
            ["window_title"] = SnapshotFormatter.Clip(snapshot.Window.Title, 120),
            ["controls"] = new JsonArray(snapshot.Elements
                .Where(e => e.IsEditable || e.IsClickable || e.Role is ElementRole.CheckBox or ElementRole.ComboBox)
                .Take(80)
                .Select(e => (JsonNode)JsonValue.Create(SnapshotFormatter.FormatElement(e, vault, 40))!)
                .ToArray()),
        };
        if (!string.IsNullOrEmpty(snapshot.Url)) { state["url"] = SnapshotFormatter.Clip(snapshot.Url, 160); }
        if (snapshot.TextBlocks.Count > 0)
        {
            state["visible_texts"] = new JsonArray(snapshot.TextBlocks.Take(12).Select(t => (JsonNode)JsonValue.Create(SnapshotFormatter.Clip(vault?.Mask(t) ?? t, 120))!).ToArray());
        }

        var response = await _jev!.DecideAsync(new JevRequest
        {
            Model = model,
            Purpose = "resolve",
            State = state,
            Questions = questions,
        }, cancellationToken).ConfigureAwait(false);
        return (response, optionsByQuestion);
    }

    private static string DescribeValue(string? value, SecretVault? vault)
    {
        if (string.IsNullOrEmpty(value)) { return "an empty value"; }
        var masked = vault?.Mask(value) ?? value;
        return $"\"{SnapshotFormatter.Clip(masked, 60)}\"";
    }

    internal static string DescribeCandidate(AgentAction step, UiElement element, SecretVault? vault)
    {
        var verb = step.Kind switch
        {
            ActionKind.SetValue => "Type into",
            ActionKind.SelectOption => "Choose an option in",
            ActionKind.SetChecked => "Toggle",
            ActionKind.Focus => "Focus",
            _ => "Click",
        };
        var line = SnapshotFormatter.FormatElement(element, vault, 30);
        // Drop the "[id] " prefix – the criterion key already identifies the element.
        var close = line.IndexOf("] ", StringComparison.Ordinal);
        return $"{verb} {(close >= 0 ? line[(close + 2)..] : line)}";
    }

    private static ResolvedStep Decide(
        AgentAction step,
        IReadOnlyList<(UiElement Element, double Score)> candidates,
        UiElement? hint,
        JevAnswer answer,
        double actThreshold,
        IReadOnlyDictionary<string, string>? options)
    {
        var top = answer.Choice ?? "";
        var pTop = answer.ProbabilityOf(top);
        UiElement? ElementFor(string key) =>
            key.StartsWith('e') && int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? candidates.FirstOrDefault(c => c.Element.Id == id).Element
                : null;

        ResolvedStep Make(UiElement? element, ResolutionSource source, double p, string? problem = null) => new()
        {
            Action = step,
            Element = element,
            Source = source,
            Probability = p,
            Confidence = answer.Confidence,
            Problem = problem,
            Options = options,
            Probabilities = answer.Probabilities,
        };

        var topElement = ElementFor(top);

        if (hint is not null)
        {
            var pHint = answer.ProbabilityOf(ElementKey(hint.Id));
            if (topElement?.Id == hint.Id) { return Make(hint, ResolutionSource.JevAgreesWithPlanner, pTop); }
            if (topElement is not null && pTop >= 0.8 && (answer.Confidence ?? 0) >= 0.6)
            {
                return Make(topElement, ResolutionSource.JevOverride, pTop);
            }
            if (pHint >= 0.2 || (top is NoneKey or ScrollKey && pTop < 0.8))
            {
                return Make(hint, ResolutionSource.PlannerPreferred, pHint);
            }
            if (top == ScrollKey) { return Make(null, ResolutionSource.NeedsScroll, pTop, "Ziel nicht sichtbar – Scrollen nötig."); }
            if (top == NoneKey) { return Make(null, ResolutionSource.Unresolved, pTop, $"Jev findet kein passendes Element für „{step.TargetLabel}“."); }
            return Make(null, ResolutionSource.Unresolved, pTop,
                $"Unklar, welches Element gemeint ist (Planer: „{hint.DisplayLabel}“, Jev: „{topElement?.DisplayLabel}“).");
        }

        if (topElement is not null && pTop >= actThreshold)
        {
            return Make(topElement, ResolutionSource.JevChoice, pTop);
        }
        if (top == ScrollKey && pTop >= 0.5) { return Make(null, ResolutionSource.NeedsScroll, pTop, "Ziel nicht sichtbar – Scrollen nötig."); }
        if (topElement is not null && pTop >= 0.5 && candidates[0].Element.Id == topElement.Id && candidates[0].Score >= 0.7)
        {
            // Jev in the "confirm" band, but the local lexical match independently agrees.
            return Make(topElement, ResolutionSource.JevChoice, pTop);
        }
        return Make(null, ResolutionSource.Unresolved, pTop,
            top == NoneKey ? $"Kein passendes Element für „{step.TargetLabel}“." : $"Jev ist unsicher ({pTop:P0}) für „{step.TargetLabel}“.");
    }

    private static ResolvedStep Fallback(AgentAction step, IReadOnlyList<(UiElement Element, double Score)> candidates, UiElement? hint)
    {
        if (hint is not null)
        {
            return new ResolvedStep { Action = step, Element = hint, Source = ResolutionSource.PlannerFallback, Probability = 0.5 };
        }
        var best = candidates[0];
        var second = candidates.Count > 1 ? candidates[1].Score : 0;
        if (best.Score >= 0.75 && best.Score - second >= 0.1)
        {
            return new ResolvedStep { Action = step, Element = best.Element, Source = ResolutionSource.LocalMatch, Probability = best.Score };
        }
        return new ResolvedStep { Action = step, Source = ResolutionSource.Unresolved, Problem = $"Ziel „{step.TargetLabel}“ nicht eindeutig." };
    }

    /// <summary>Two text steps must not write into the same field: the less likely one loses.</summary>
    private static void ResolveConflicts(List<ResolvedStep> steps)
    {
        var groups = steps.Select((s, i) => (s, i))
            .Where(x => x.s.IsResolved && x.s.Action.Kind == ActionKind.SetValue)
            .GroupBy(x => x.s.Element!.Id)
            .Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(x => x.s.Probability).ThenBy(x => x.s.Source == ResolutionSource.JevAgreesWithPlanner ? 0 : 1).ToList();
            foreach (var loser in ordered.Skip(1))
            {
                steps[loser.i] = loser.s with
                {
                    Element = null,
                    Source = ResolutionSource.Unresolved,
                    Problem = $"„{loser.s.Action.TargetLabel}“ würde dasselbe Feld wie ein anderer Schritt überschreiben.",
                };
            }
        }
    }

    /// <summary>
    /// Re-maps a step onto a new snapshot after the UI changed (same locator, or same label/role).
    /// Returns null when the element cannot be found reliably.
    /// </summary>
    public static UiElement? Remap(UiElement previous, AgentAction step, UiSnapshot snapshot)
    {
        var byLocator = snapshot.FindByLocator(previous.Locator);
        if (byLocator is not null && ElementMatcher.IsCompatible(step.Kind, byLocator)) { return byLocator; }
        var best = snapshot.Elements
            .Where(e => e.Role == previous.Role && ElementMatcher.IsCompatible(step.Kind, e))
            .Select(e => (e, Score: ElementMatcher.TextSimilarity(ElementMatcher.Normalize(previous.DisplayLabel), ElementMatcher.Normalize(e.DisplayLabel))))
            .OrderByDescending(x => x.Score)
            .ToList();
        if (best.Count == 0 || best[0].Score < 0.9) { return null; }
        if (best.Count > 1 && best[1].Score >= 0.9) { return null; } // ambiguous
        return best[0].e;
    }
}
