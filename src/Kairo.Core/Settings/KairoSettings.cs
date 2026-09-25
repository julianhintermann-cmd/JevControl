using System.Text.Json.Serialization;
using Kairo.Core.AI.OpenRouter;

namespace Kairo.Core.Settings;

/// <summary>
/// User settings (stored as JSON in %APPDATA%\Kairo\settings.json).
/// Contains NO secrets – the API key lives in the DPAPI protected secret store.
/// </summary>
public sealed class KairoSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool OnboardingCompleted { get; set; }
    public GeneralSettings General { get; set; } = new();
    public ModelSettings Models { get; set; } = new();
    public ControlSettings Control { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public KairoSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(this, SettingsJsonContext.Default.KairoSettings), SettingsJsonContext.Default.KairoSettings)!;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool ShowTrayNotifications { get; set; } = true;
    /// <summary>Hide the overlay automatically a few seconds after a task finished successfully.</summary>
    public bool AutoHideOnSuccess { get; set; } = true;
    public int AutoHideSeconds { get; set; } = 4;
}

public sealed class ModelSettings
{
    /// <summary>Generative model that interprets the instruction and creates the task plan.</summary>
    public string PlannerModel { get; set; } = DefaultModels.Planner;
    /// <summary>Jev decision model (OpenRouter Decisions API).</summary>
    public string DecisionModel { get; set; } = DefaultModels.Decision;
    /// <summary>Optional vision model for the screenshot fallback.</summary>
    public string VisionModel { get; set; } = DefaultModels.Vision;
    public bool VisionEnabled { get; set; } = true;
    /// <summary>Optional reasoning effort for the planner ("low", "medium", "high"); empty = model default.</summary>
    public string? PlannerReasoningEffort { get; set; }
    /// <summary>Use Jev for target resolution, verification and risk checks.</summary>
    public bool UseJevDecisions { get; set; } = true;
    /// <summary>Jev choice probability required to act without planner agreement.</summary>
    public double JevActThreshold { get; set; } = 0.7;
}

public sealed class ControlSettings
{
    public int MaxActionsPerTask { get; set; } = 60;
    public int MaxPlanningRounds { get; set; } = 10;
    public int MaxRetriesPerStep { get; set; } = 2;
    public int ActionTimeoutSeconds { get; set; } = 20;
    /// <summary>Delay between simulated key strokes (0 = as fast as possible).</summary>
    public int TypingDelayMs { get; set; }
    public bool UseBrowserExtension { get; set; } = true;
    /// <summary>Start perceiving the active window as soon as the overlay opens (lower latency).</summary>
    public bool PrefetchOnOverlayOpen { get; set; } = true;
    public int MaxSnapshotElements { get; set; } = 250;
}

public enum ApprovalMode
{
    /// <summary>Ask for sensitive and irreversible actions (recommended).</summary>
    SensitiveAndIrreversible,
    /// <summary>Ask for every action that changes something outside the focused form.</summary>
    Always,
}

public sealed class SecuritySettings
{
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.SensitiveAndIrreversible;
    /// <summary>Folders Kairo may read from. Empty = the user's standard folders.</summary>
    public List<string> AllowedReadRoots { get; set; } = [];
    /// <summary>Folders Kairo may write to (create/rename/move/delete). Empty = the user's standard folders.</summary>
    public List<string> AllowedWriteRoots { get; set; } = [];
    /// <summary>Applications (process names) Kairo must never control.</summary>
    public List<string> BlockedApplications { get; set; } =
        ["keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "credentialuibroker", "consent"];
    /// <summary>Maximum number of files a single delete may affect before it is refused.</summary>
    public int MaxFilesPerDelete { get; set; } = 20;
    public long MaxReadFileBytes { get; set; } = 25 * 1024 * 1024;
    /// <summary>Use Jev as an additional risk classifier for ambiguous buttons.</summary>
    public bool UseJevRiskCheck { get; set; } = true;
    /// <summary>Scan untrusted content (web pages, files) for embedded instructions.</summary>
    public bool DetectPromptInjection { get; set; } = true;
}

public enum VisionConsent
{
    /// <summary>Screenshots are sent without asking but always shown in the overlay.</summary>
    Notify,
    /// <summary>Ask before each screenshot is sent.</summary>
    Ask,
    /// <summary>Never send screenshots.</summary>
    Never,
}

public sealed class PrivacySettings
{
    public VisionConsent VisionConsent { get; set; } = VisionConsent.Notify;
    public bool MaskPasswordFieldsInScreenshots { get; set; } = true;
    /// <summary>Mask credit card numbers, IBANs and similar before sending text to external models.</summary>
    public bool MaskSensitiveNumbers { get; set; } = true;
    /// <summary>Maximum characters of a local file that may be sent to the planner.</summary>
    public int MaxFileCharsToModel { get; set; } = 16_000;
    public bool SaveTaskHistory { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    /// <summary>Store the instruction text in the history (otherwise only metadata).</summary>
    public bool StoreInstructionsInHistory { get; set; } = true;
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum OverlayPlacement
{
    TopCenter,
    Center,
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public OverlayPlacement OverlayPlacement { get; set; } = OverlayPlacement.TopCenter;
    public bool Animations { get; set; } = true;
    public bool UseMica { get; set; } = true;
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyBinding DefaultOverlay = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K");
    public static readonly HotkeyBinding DefaultEmergencyStop = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "K");

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) { parts.Add("Strg"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) { parts.Add("Alt"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) { parts.Add("Umschalt"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) { parts.Add("Win"); }
        parts.Add(Key);
        return string.Join(" + ", parts);
    }
}

public sealed class HotkeySettings
{
    public HotkeyBinding Overlay { get; set; } = HotkeyBinding.DefaultOverlay;
    /// <summary>Stops a running task immediately, even when the overlay is hidden.</summary>
    public HotkeyBinding EmergencyStop { get; set; } = HotkeyBinding.DefaultEmergencyStop;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KairoSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
