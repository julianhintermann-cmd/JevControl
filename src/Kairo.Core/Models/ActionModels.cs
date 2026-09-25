using System.Text.Json.Serialization;

namespace Kairo.Core.Models;

/// <summary>Every action Kairo can execute. The planner may only produce actions from this catalog.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionKind>))]
public enum ActionKind
{
    // --- UI element actions (need a target element of the current snapshot) ---
    SetValue,
    Click,
    Focus,
    SelectOption,
    SetChecked,
    Scroll,
    // --- Input actions ---
    TypeText,
    Hotkey,
    MouseClick,
    // --- Windows / applications ---
    LaunchApp,
    SwitchWindow,
    WindowState,
    CloseWindow,
    OpenFile,
    OpenUrl,
    BrowserTab,
    // --- Files ---
    ReadFile,
    SearchFiles,
    ListFolder,
    CreateFolder,
    RenameFile,
    MoveFile,
    CopyFile,
    DeleteFile,
    WriteTextFile,
    // --- Clipboard ---
    ClipboardSet,
    ClipboardGet,
    // --- Control flow ---
    Wait,
    RequestVision,
    AskUser,
    Finish,
}

public static class ActionKindExtensions
{
    /// <summary>Actions that operate on an element of the current snapshot and are resolved by Jev.</summary>
    public static bool IsElementAction(this ActionKind kind) =>
        kind is ActionKind.SetValue or ActionKind.Click or ActionKind.Focus or ActionKind.SelectOption or ActionKind.SetChecked;

    /// <summary>Actions that only gather information and therefore trigger a re-plan with the result.</summary>
    public static bool IsInformational(this ActionKind kind) =>
        kind is ActionKind.ReadFile or ActionKind.SearchFiles or ActionKind.ListFolder or ActionKind.ClipboardGet;

    /// <summary>Actions after which the UI structure most likely changed.</summary>
    public static bool ChangesStructure(this ActionKind kind) =>
        kind is ActionKind.Click or ActionKind.LaunchApp or ActionKind.SwitchWindow or ActionKind.CloseWindow or
            ActionKind.OpenFile or ActionKind.OpenUrl or ActionKind.BrowserTab or ActionKind.Hotkey or
            ActionKind.MouseClick or ActionKind.WindowState or ActionKind.Scroll;

    public static bool IsFileAction(this ActionKind kind) =>
        kind is ActionKind.ReadFile or ActionKind.SearchFiles or ActionKind.ListFolder or ActionKind.CreateFolder or
            ActionKind.RenameFile or ActionKind.MoveFile or ActionKind.CopyFile or ActionKind.DeleteFile or
            ActionKind.WriteTextFile;

    /// <summary>snake_case name used in prompts and JSON.</summary>
    public static string ToWireName(this ActionKind kind) => kind switch
    {
        ActionKind.SetValue => "set_value",
        ActionKind.Click => "click",
        ActionKind.Focus => "focus",
        ActionKind.SelectOption => "select_option",
        ActionKind.SetChecked => "set_checked",
        ActionKind.Scroll => "scroll",
        ActionKind.TypeText => "type_text",
        ActionKind.Hotkey => "hotkey",
        ActionKind.MouseClick => "mouse_click",
        ActionKind.LaunchApp => "launch_app",
        ActionKind.SwitchWindow => "switch_window",
        ActionKind.WindowState => "window_state",
        ActionKind.CloseWindow => "close_window",
        ActionKind.OpenFile => "open_file",
        ActionKind.OpenUrl => "open_url",
        ActionKind.BrowserTab => "browser_tab",
        ActionKind.ReadFile => "read_file",
        ActionKind.SearchFiles => "search_files",
        ActionKind.ListFolder => "list_folder",
        ActionKind.CreateFolder => "create_folder",
        ActionKind.RenameFile => "rename_file",
        ActionKind.MoveFile => "move_file",
        ActionKind.CopyFile => "copy_file",
        ActionKind.DeleteFile => "delete_file",
        ActionKind.WriteTextFile => "write_text_file",
        ActionKind.ClipboardSet => "clipboard_set",
        ActionKind.ClipboardGet => "clipboard_get",
        ActionKind.Wait => "wait",
        ActionKind.RequestVision => "request_vision",
        ActionKind.AskUser => "ask_user",
        ActionKind.Finish => "finish",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static readonly Dictionary<string, ActionKind> ByWireName =
        Enum.GetValues<ActionKind>().ToDictionary(k => k.ToWireName(), k => k, StringComparer.OrdinalIgnoreCase);

    public static bool TryParseWireName(string? name, out ActionKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(name)) { return false; }
        var normalized = name.Trim().Replace('-', '_').Replace(' ', '_');
        if (ByWireName.TryGetValue(normalized, out kind)) { return true; }
        return Enum.TryParse(normalized.Replace("_", ""), ignoreCase: true, out kind);
    }

    public static IReadOnlyCollection<string> AllWireNames => ByWireName.Keys;
}

/// <summary>A concrete, validated action. Produced by the planner, resolved by Jev, checked by the permission manager.</summary>
public sealed record AgentAction
{
    public required ActionKind Kind { get; init; }
    /// <summary>Target element id in the snapshot the action was planned/resolved against.</summary>
    public int? TargetId { get; init; }
    /// <summary>Human readable description of the intended target ("Feld E-Mail"). Used for re-resolution.</summary>
    public string? TargetLabel { get; init; }
    public string? Value { get; init; }
    public string? Path { get; init; }
    public string? Destination { get; init; }
    public string? Keys { get; init; }
    public string? Url { get; init; }
    public string? App { get; init; }
    public string? Direction { get; init; }
    public int? Amount { get; init; }
    public bool? Checked { get; init; }
    public string? Option { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    /// <summary>Short human readable description, shown in the UI ("Name eintragen").</summary>
    public string Description { get; init; } = "";
    /// <summary>What should be observable after the action (used for verification of clicks).</summary>
    public string? Expectation { get; init; }

    public override string ToString()
    {
        var parts = new List<string> { Kind.ToWireName() };
        if (TargetId is { } t) { parts.Add($"target=[{t}]"); }
        if (!string.IsNullOrEmpty(TargetLabel)) { parts.Add($"label=\"{TargetLabel}\""); }
        if (Kind is ActionKind.SetValue or ActionKind.TypeText) { parts.Add($"value=({Value?.Length ?? 0} chars)"); }
        if (!string.IsNullOrEmpty(Option)) { parts.Add($"option=\"{Option}\""); }
        if (Checked is { } c) { parts.Add($"checked={c}"); }
        if (!string.IsNullOrEmpty(Keys)) { parts.Add($"keys={Keys}"); }
        if (!string.IsNullOrEmpty(App)) { parts.Add($"app={App}"); }
        return string.Join(' ', parts);
    }
}

public enum ActionErrorKind
{
    None,
    ElementNotFound,
    NotSupported,
    Denied,
    Timeout,
    Failed,
    Cancelled,
    InvalidArguments,
}

/// <summary>Result of executing a single action.</summary>
public sealed record ActionResult
{
    public required bool Success { get; init; }
    public string Message { get; init; } = "";
    public ActionErrorKind Error { get; init; }
    /// <summary>Data returned by informational actions (file content, search results, clipboard). Untrusted.</summary>
    public string? Data { get; init; }
    /// <summary>Strategy that finally worked (e.g. "ValuePattern", "DOM", "SendInput").</summary>
    public string? Strategy { get; init; }
    /// <summary>True when the action most likely changed the UI structure (navigation, dialog, new window).</summary>
    public bool StructureChanged { get; init; }
    /// <summary>A new foreground/target window, e.g. after launch_app or switch_window.</summary>
    public WindowInfo? NewTargetWindow { get; init; }
    public TimeSpan Duration { get; init; }

    public static ActionResult Ok(string message = "", string? strategy = null, bool structureChanged = false) =>
        new() { Success = true, Message = message, Strategy = strategy, StructureChanged = structureChanged };

    public static ActionResult Fail(ActionErrorKind error, string message) =>
        new() { Success = false, Error = error, Message = message };
}
