using System.Text.Json.Nodes;
using Kairo.Core.AI.Jev;
using Kairo.Core.Models;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Security;

/// <summary>Security state of one task.</summary>
public sealed class TaskSecurityContext
{
    public TaskSecurityContext(string instruction, FileAccessPolicy fileAccess, SecretVault vault)
    {
        Instruction = instruction;
        FileAccess = fileAccess;
        Vault = vault;
    }

    public string Instruction { get; }
    public FileAccessPolicy FileAccess { get; }
    public SecretVault Vault { get; }
    public UntrustedContent Untrusted { get; } = new();

    /// <summary>Prompt injection findings from web pages or files read during the task.</summary>
    public List<InjectionFinding> InjectionFindings { get; } = [];

    public bool InjectionSuspected => InjectionFindings.Count > 0;

    /// <summary>The user chose "Für diese Aufgabe erlauben" for sensitive actions.</summary>
    public bool SensitiveApprovedForTask { get; set; }

    /// <summary>Records untrusted text and scans it for embedded instructions.</summary>
    public void Inspect(string source, string? content)
    {
        foreach (var finding in InjectionDetector.Scan(source, content))
        {
            if (!InjectionFindings.Any(f => f.Source == finding.Source && f.Pattern == finding.Pattern))
            {
                InjectionFindings.Add(finding);
            }
        }
    }
}

public sealed record PermissionDecision
{
    public required RiskLevel Risk { get; init; }
    public required bool RequiresApproval { get; init; }
    public bool Blocked => Risk == RiskLevel.Forbidden;
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public double? JevRiskProbability { get; init; }
}

/// <summary>
/// Central permission manager. Every action passes through <see cref="EvaluateAsync"/> before execution.
/// Normal actions run automatically inside an authorized task; sensitive ones need an explicit approval
/// (optionally for the rest of the task); irreversible ones need an approval every time; forbidden ones never run.
/// </summary>
public sealed class PermissionManager
{
    private readonly IDecisionModel? _decisions;
    private readonly Func<KairoSettings> _settings;
    private readonly KairoLogger _log;

    public PermissionManager(IDecisionModel? decisions, Func<KairoSettings> settings, KairoLogger log)
    {
        _decisions = decisions;
        _settings = settings;
        _log = log;
    }

    public async Task<PermissionDecision> EvaluateAsync(AgentAction action, UiElement? element, UiSnapshot? snapshot, TaskSecurityContext context, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var assessment = RiskClassifier.Classify(action, element, snapshot, settings.Security.BlockedApplications);
        var reasons = assessment.Reasons.ToList();
        var level = assessment.Level;
        double? jevProbability = null;

        if (level == RiskLevel.Forbidden)
        {
            return Build(action, element, snapshot, level, requiresApproval: false, reasons, null);
        }

        // File boundaries.
        if (action.Kind.IsFileAction() || action.Kind == ActionKind.OpenFile)
        {
            var fileLevel = EvaluateFiles(action, context, reasons);
            if (fileLevel > level) { level = fileLevel; }
            if (level == RiskLevel.Forbidden)
            {
                return Build(action, element, snapshot, level, requiresApproval: false, reasons, null);
            }
        }

        // URLs that did not come from the user while untrusted content tried to give instructions.
        if (action.Kind == ActionKind.OpenUrl && context.InjectionSuspected && !MentionedByUser(action.Url, context.Instruction))
        {
            level = Max(level, RiskLevel.Sensitive);
            reasons.Add("Die Adresse stammt nicht aus deiner Anweisung und die Seite enthielt verdächtige Anweisungen.");
        }

        // Jev as second opinion for ambiguous buttons ("OK", "Weiter", unlabeled).
        if (assessment.Ambiguous && settings.Security.UseJevRiskCheck && settings.Models.UseJevDecisions && _decisions is not null &&
            action.Kind is ActionKind.Click or ActionKind.MouseClick)
        {
            jevProbability = await AskJevAsync(action, element, snapshot, settings, cancellationToken).ConfigureAwait(false);
            if (jevProbability is >= 0.6)
            {
                level = Max(level, RiskLevel.Sensitive);
                reasons.Add($"Jev stuft die Aktion als folgenreich ein ({RiskClassifier.FormatPercent(jevProbability.Value)}).");
            }
        }

        var requiresApproval = level switch
        {
            RiskLevel.Irreversible => true,
            RiskLevel.Sensitive => !context.SensitiveApprovedForTask || context.InjectionSuspected,
            _ => settings.Security.ApprovalMode == ApprovalMode.Always &&
                 action.Kind is ActionKind.Click or ActionKind.MouseClick or ActionKind.Hotkey or ActionKind.LaunchApp or
                     ActionKind.OpenFile or ActionKind.OpenUrl or ActionKind.CloseWindow or ActionKind.MoveFile or
                     ActionKind.RenameFile or ActionKind.CopyFile or ActionKind.CreateFolder or ActionKind.WriteTextFile,
        };

        if (context.InjectionSuspected && level >= RiskLevel.Sensitive)
        {
            reasons.Add("Achtung: Gelesene Inhalte enthielten mögliche versteckte Anweisungen an KI-Systeme.");
        }

        return Build(action, element, snapshot, level, requiresApproval, reasons, jevProbability);
    }

    private static RiskLevel Max(RiskLevel a, RiskLevel b) => a > b ? a : b;

    private RiskLevel EvaluateFiles(AgentAction action, TaskSecurityContext context, List<string> reasons)
    {
        var level = RiskLevel.Normal;
        var paths = new List<(string Path, FileAccessKind Kind)>();
        switch (action.Kind)
        {
            case ActionKind.ReadFile:
            case ActionKind.ListFolder:
            case ActionKind.SearchFiles:
            case ActionKind.OpenFile:
                if (!string.IsNullOrWhiteSpace(action.Path)) { paths.Add((action.Path, FileAccessKind.Read)); }
                break;
            case ActionKind.CreateFolder:
            case ActionKind.WriteTextFile:
            case ActionKind.DeleteFile:
                if (!string.IsNullOrWhiteSpace(action.Path)) { paths.Add((action.Path, FileAccessKind.Write)); }
                break;
            case ActionKind.RenameFile:
            case ActionKind.MoveFile:
                if (!string.IsNullOrWhiteSpace(action.Path)) { paths.Add((action.Path, FileAccessKind.Write)); }
                if (!string.IsNullOrWhiteSpace(action.Destination)) { paths.Add((action.Destination, FileAccessKind.Write)); }
                break;
            case ActionKind.CopyFile:
                if (!string.IsNullOrWhiteSpace(action.Path)) { paths.Add((action.Path, FileAccessKind.Read)); }
                if (!string.IsNullOrWhiteSpace(action.Destination)) { paths.Add((action.Destination, FileAccessKind.Write)); }
                break;
        }

        foreach (var (path, kind) in paths)
        {
            var decision = context.FileAccess.Evaluate(path, kind);
            switch (decision.Verdict)
            {
                case FileAccessVerdict.Denied:
                    reasons.Add($"{decision.Reason} ({decision.NormalizedPath})");
                    return RiskLevel.Forbidden;
                case FileAccessVerdict.NeedsApproval:
                    level = Max(level, RiskLevel.Sensitive);
                    reasons.Add($"{decision.Reason} ({decision.NormalizedPath})");
                    break;
            }
        }

        if (action.Kind is ActionKind.MoveFile or ActionKind.CopyFile or ActionKind.WriteTextFile or ActionKind.RenameFile &&
            TargetExists(action))
        {
            level = Max(level, RiskLevel.Sensitive);
            reasons.Add("Eine vorhandene Datei würde überschrieben.");
        }

        if (action.Kind == ActionKind.DeleteFile && !string.IsNullOrWhiteSpace(action.Path))
        {
            var count = CountFiles(action.Path, _settings().Security.MaxFilesPerDelete + 1);
            if (count > _settings().Security.MaxFilesPerDelete)
            {
                reasons.Add($"Umfangreiche Löschung (mehr als {_settings().Security.MaxFilesPerDelete} Dateien) ist gesperrt.");
                return RiskLevel.Forbidden;
            }
            if (count > 1 || Directory.Exists(action.Path))
            {
                level = Max(level, RiskLevel.Irreversible);
                reasons.Add($"Ordner bzw. {count} Dateien werden gelöscht.");
            }
        }

        return level;
    }

    private static bool TargetExists(AgentAction action)
    {
        try
        {
            var target = action.Kind == ActionKind.WriteTextFile ? action.Path : action.Destination;
            if (string.IsNullOrWhiteSpace(target)) { return false; }
            if (action.Kind == ActionKind.RenameFile && action.Path is not null)
            {
                var dir = Path.GetDirectoryName(FileAccessPolicy.NormalizePath(action.Path)) ?? "";
                target = Path.Combine(dir, target);
            }
            var full = FileAccessPolicy.NormalizePath(target);
            if (Directory.Exists(full) && action.Path is not null && action.Kind is ActionKind.MoveFile or ActionKind.CopyFile)
            {
                full = Path.Combine(full, Path.GetFileName(action.Path));
            }
            return File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int CountFiles(string path, int limit)
    {
        try
        {
            var full = FileAccessPolicy.NormalizePath(path);
            if (File.Exists(full)) { return 1; }
            if (!Directory.Exists(full)) { return 0; }
            return Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Take(limit).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return limit;
        }
    }

    private static bool MentionedByUser(string? url, string instruction)
    {
        if (string.IsNullOrWhiteSpace(url)) { return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { return false; }
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return instruction.Contains(host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<double?> AskJevAsync(AgentAction action, UiElement? element, UiSnapshot? snapshot, KairoSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var state = new JsonObject
            {
                ["application"] = snapshot?.Window.ProcessName,
                ["window_title"] = snapshot?.Window.Title,
                ["url"] = snapshot?.Url,
                ["control"] = new JsonObject
                {
                    ["role"] = element?.Role.ToString(),
                    ["label"] = element?.DisplayLabel ?? action.TargetLabel,
                    ["section"] = element?.Section,
                },
                ["planned_step"] = action.Description,
                ["form_fields"] = new JsonArray((snapshot?.Elements ?? [])
                    .Where(e => e.Role is ElementRole.Edit or ElementRole.ComboBox or ElementRole.CheckBox)
                    .Take(25)
                    .Select(e => (JsonNode)JsonValue.Create(e.DisplayLabel)!)
                    .ToArray()),
                ["page_texts"] = new JsonArray((snapshot?.TextBlocks ?? []).Take(10).Select(t => (JsonNode)JsonValue.Create(t.Length > 160 ? t[..160] : t)!).ToArray()),
            };

            var response = await _decisions!.DecideAsync(new JevRequest
            {
                Model = settings.Models.DecisionModel,
                Purpose = "risk",
                State = state,
                Questions = new Dictionary<string, JevQuestion>
                {
                    ["consequential"] = JevQuestion.Noul(
                        "Would activating this control send data or a message to another person or organization, submit a form, make a purchase or payment, delete data, or change security settings?",
                        "Submits, sends, pays, buys, deletes or changes security settings",
                        "Only navigates, opens, expands, closes a dialog without consequences, or edits local input"),
                },
            }, cancellationToken).ConfigureAwait(false);
            return response["consequential"]?.Noul;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("permissions", $"Jev risk check unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private static PermissionDecision Build(AgentAction action, UiElement? element, UiSnapshot? snapshot, RiskLevel level, bool requiresApproval, List<string> reasons, double? jev)
    {
        var target = element?.DisplayLabel is { Length: > 0 } label ? $"„{label}“" : action.TargetLabel is { Length: > 0 } tl ? $"„{tl}“" : null;
        var app = snapshot?.Window.AppName;
        var title = level switch
        {
            RiskLevel.Forbidden => "Aktion gesperrt",
            RiskLevel.Irreversible => "Nicht umkehrbare Aktion freigeben?",
            RiskLevel.Sensitive => "Sensible Aktion freigeben?",
            _ => "Aktion freigeben?",
        };
        var description = DescribeAction(action, target, app);
        return new PermissionDecision
        {
            Risk = level,
            RequiresApproval = requiresApproval,
            Reasons = reasons,
            Title = title,
            Description = description,
            JevRiskProbability = jev,
        };
    }

    /// <summary>Plain German description of what will happen.</summary>
    public static string DescribeAction(AgentAction action, string? target, string? app)
    {
        var inApp = string.IsNullOrEmpty(app) ? "" : $" in {app}";
        return action.Kind switch
        {
            ActionKind.Click or ActionKind.MouseClick => $"Kairo klickt auf {target ?? "ein Element"}{inApp}.",
            ActionKind.SetValue => $"Kairo trägt einen Wert in {target ?? "ein Feld"}{inApp} ein.",
            ActionKind.TypeText => $"Kairo tippt Text{inApp}.",
            ActionKind.Hotkey => $"Kairo drückt {action.Keys}{inApp}.",
            ActionKind.LaunchApp => $"Kairo startet „{action.App ?? action.Path}“.",
            ActionKind.OpenFile => $"Kairo öffnet „{action.Path}“.",
            ActionKind.OpenUrl => $"Kairo öffnet die Adresse {action.Url}.",
            ActionKind.DeleteFile => $"Kairo verschiebt „{action.Path}“ in den Papierkorb.",
            ActionKind.MoveFile => $"Kairo verschiebt „{action.Path}“ nach „{action.Destination}“.",
            ActionKind.RenameFile => $"Kairo benennt „{action.Path}“ in „{action.Destination ?? action.Value}“ um.",
            ActionKind.CopyFile => $"Kairo kopiert „{action.Path}“ nach „{action.Destination}“.",
            ActionKind.WriteTextFile => $"Kairo schreibt die Datei „{action.Path}“.",
            ActionKind.CreateFolder => $"Kairo erstellt den Ordner „{action.Path}“.",
            ActionKind.ReadFile => $"Kairo liest „{action.Path}“.",
            ActionKind.ListFolder or ActionKind.SearchFiles => $"Kairo durchsucht „{action.Path}“.",
            ActionKind.SetChecked => $"Kairo {(action.Checked == false ? "deaktiviert" : "aktiviert")} {target ?? "ein Kontrollkästchen"}{inApp}.",
            ActionKind.SelectOption => $"Kairo wählt „{action.Option ?? action.Value}“ in {target ?? "einer Auswahl"}{inApp}.",
            ActionKind.CloseWindow => $"Kairo schließt ein Fenster{inApp}.",
            _ => string.IsNullOrWhiteSpace(action.Description) ? action.ToString() : action.Description,
        };
    }
}
