using Kairo.Core.Models;

namespace Kairo.Core.Abstractions;

/// <summary>Access to top-level windows of the desktop.</summary>
public interface IWindowService
{
    WindowInfo? GetForegroundWindow();
    WindowInfo? GetWindow(nint handle);
    IReadOnlyList<WindowInfo> ListWindows();
    bool IsWindowAlive(nint handle);
    Task<bool> ActivateAsync(nint handle, CancellationToken cancellationToken);
}

/// <summary>Options for capturing a snapshot.</summary>
public sealed record PerceptionRequest
{
    public int MaxElements { get; init; } = 250;
    public bool IncludeText { get; init; } = true;
    public bool IncludeOffscreen { get; init; } = true;
}

/// <summary>Current state of a single element, read back for verification.</summary>
public sealed record ElementState(bool Exists, string? Value = null, bool? IsChecked = null, string? SelectedOption = null);

/// <summary>A perception stage (UI Automation, Browser DOM, Vision).</summary>
public interface IPerceptionProvider
{
    PerceptionSource Source { get; }

    /// <summary>Higher values are tried first.</summary>
    int Priority { get; }

    bool CanHandle(WindowInfo window);

    /// <summary>Returns null when the provider cannot produce a useful snapshot for the window.</summary>
    Task<UiSnapshot?> CaptureAsync(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Targeted read-back of individual elements (cheap, no full snapshot). Keyed by <see cref="UiElement.Locator"/>,
    /// so elements of an older snapshot of the same window can be verified as well.
    /// </summary>
    Task<IReadOnlyDictionary<string, ElementState>> ReadStatesAsync(UiSnapshot snapshot, IReadOnlyCollection<UiElement> elements, CancellationToken cancellationToken);
}

/// <summary>Raised by platform monitors when the UI of a watched window changed (event based cache invalidation).</summary>
public sealed class UiChangedEventArgs(nint windowHandle, UiChangeKind kind) : EventArgs
{
    public nint WindowHandle { get; } = windowHandle;
    public UiChangeKind Kind { get; } = kind;
}

public enum UiChangeKind
{
    Structure,
    Values,
    Focus,
    WindowClosed,
    Navigation,
}

public interface IUiChangeMonitor
{
    event EventHandler<UiChangedEventArgs>? Changed;

    /// <summary>Start watching a window. Only one window is watched at a time; watching stops when the task ends.</summary>
    void Watch(WindowInfo window);

    void StopWatching();
}

/// <summary>Context passed to the executor for each action.</summary>
public sealed class ActionContext
{
    public required WindowInfo TargetWindow { get; set; }
    public UiSnapshot? Snapshot { get; set; }
    public required ExecutionGate Gate { get; init; }
    /// <summary>Correction mode: bypass structured APIs and simulate real keyboard/mouse input.</summary>
    public bool PreferInputSimulation { get; set; }
    /// <summary>Delay between simulated key strokes.</summary>
    public int TypingDelayMs { get; set; }
}

/// <summary>Executes actions on the real system.</summary>
public interface IActionExecutor
{
    Task<ActionResult> ExecuteAsync(AgentAction action, ActionContext context, CancellationToken cancellationToken);

    /// <summary>Executes several element actions (fills, checks, selects) against the same snapshot in one fast pass.</summary>
    Task<IReadOnlyList<ActionResult>> ExecuteElementBatchAsync(IReadOnlyList<AgentAction> actions, ActionContext context, CancellationToken cancellationToken);
}

/// <summary>Captured window image for the vision fallback.</summary>
public sealed record CapturedImage(byte[] Data, string MimeType, int Width, int Height, ScreenRect ScreenRegion, double Scale);

public sealed record CaptureOptions
{
    /// <summary>Longest image edge after downscaling.</summary>
    public int MaxEdge { get; init; } = 1280;
    /// <summary>Screen rectangles that must be blacked out (password fields, secrets).</summary>
    public IReadOnlyList<ScreenRect> MaskRegions { get; init; } = [];
    /// <summary>Optional sub region of the window (screen coordinates). Null = whole window.</summary>
    public ScreenRect? Region { get; init; }
}

public interface IScreenCapture
{
    Task<CapturedImage?> CaptureWindowAsync(WindowInfo window, CaptureOptions options, CancellationToken cancellationToken);
}

/// <summary>Encrypted secret storage (Windows: DPAPI, bound to the current user).</summary>
public interface ISecretStore
{
    bool HasSecret(string name);
    string? GetSecret(string name);
    void SetSecret(string name, string value);
    /// <summary>Removes the secret. The encrypted blob is overwritten before deletion.</summary>
    void DeleteSecret(string name);
}

/// <summary>User interaction during a task (approvals, questions). Implemented by the overlay.</summary>
public interface IUserInteraction
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
    Task<string?> AskUserAsync(string question, CancellationToken cancellationToken);
}

public enum ApprovalDecision
{
    Deny,
    AllowOnce,
    AllowForTask,
}

public sealed record ApprovalRequest
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Security.RiskLevel Risk { get; init; }
    public required AgentAction Action { get; init; }
    public string? TargetDescription { get; init; }
    public string? ApplicationName { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
