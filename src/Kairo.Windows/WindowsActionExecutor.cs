using System.Diagnostics;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Kairo.Windows.Apps;
using Kairo.Windows.Automation;
using Kairo.Windows.Browser;
using Kairo.Windows.Input;
using Kairo.Windows.Security;
using Kairo.Windows.Windows;

namespace Kairo.Windows;

/// <summary>
/// Executes Kairo actions on the real Windows desktop. Element actions are routed by perception source
/// (UI Automation patterns → DOM through the extension → coordinates for vision snapshots); system actions use
/// Win32/Shell APIs. Every step re-checks the execution gate, so a cancelled task cannot act anymore.
/// </summary>
public sealed class WindowsActionExecutor : IActionExecutor
{
    private readonly UiaElementActions _uia;
    private readonly BrowserElementActions _dom;
    private readonly WindowService _windows;
    private readonly InputSimulator _input;
    private readonly AppLauncher _launcher;
    private readonly ClipboardService _clipboard;
    private readonly KairoLogger _log;

    public WindowsActionExecutor(UiaElementActions uia, BrowserElementActions dom, WindowService windows, InputSimulator input, AppLauncher launcher, ClipboardService clipboard, KairoLogger log)
    {
        _uia = uia;
        _dom = dom;
        _windows = windows;
        _input = input;
        _launcher = launcher;
        _clipboard = clipboard;
        _log = log;
    }

    public async Task<ActionResult> ExecuteAsync(AgentAction action, ActionContext context, CancellationToken cancellationToken)
    {
        context.Gate.ThrowIfClosed();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = action.Kind.IsElementAction()
                ? await ExecuteElementAsync(action, context, cancellationToken).ConfigureAwait(false)
                : await ExecuteSystemAsync(action, context, cancellationToken).ConfigureAwait(false);
            _log.Debug("exec", $"{action.Kind} ok={result.Success} via={result.Strategy} ms={sw.ElapsedMilliseconds}");
            return result with { Duration = sw.Elapsed };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("blockiert", StringComparison.Ordinal))
        {
            return ActionResult.Fail(ActionErrorKind.Denied, ex.Message);
        }
        catch (FormatException ex)
        {
            return ActionResult.Fail(ActionErrorKind.InvalidArguments, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ActionResult>> ExecuteElementBatchAsync(IReadOnlyList<AgentAction> actions, ActionContext context, CancellationToken cancellationToken)
    {
        context.Gate.ThrowIfClosed();
        var snapshot = context.Snapshot;
        if (snapshot?.Source == PerceptionSource.BrowserDom && !context.PreferInputSimulation)
        {
            var items = new List<(AgentAction, UiElement)>();
            foreach (var a in actions)
            {
                var element = a.TargetId is { } id ? snapshot.Find(id) : null;
                if (element is null)
                {
                    return await SequentialAsync(actions, context, cancellationToken).ConfigureAwait(false);
                }
                items.Add((a, element));
            }
            return await _dom.ExecuteBatchAsync(items, context, cancellationToken).ConfigureAwait(false);
        }
        return await SequentialAsync(actions, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ActionResult>> SequentialAsync(IReadOnlyList<AgentAction> actions, ActionContext context, CancellationToken cancellationToken)
    {
        var results = new List<ActionResult>(actions.Count);
        foreach (var a in actions)
        {
            context.Gate.ThrowIfClosed();
            results.Add(await ExecuteAsync(a, context, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    // ------------------------------------------------------------------ element actions
    private async Task<ActionResult> ExecuteElementAsync(AgentAction action, ActionContext context, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot;
        var element = action.TargetId is { } id ? snapshot?.Find(id) : null;
        if (snapshot is null || element is null)
        {
            return ActionResult.Fail(ActionErrorKind.ElementNotFound, "Das Zielelement ist im aktuellen Zustand nicht vorhanden.");
        }

        return snapshot.Source switch
        {
            PerceptionSource.BrowserDom => await _dom.ExecuteAsync(action, element, context, cancellationToken).ConfigureAwait(false),
            PerceptionSource.Vision => await ExecuteVisionAsync(action, element, context, cancellationToken).ConfigureAwait(false),
            _ => await _uia.ExecuteAsync(action, element, context, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Vision snapshots only know positions: act with real mouse/keyboard input.</summary>
    private async Task<ActionResult> ExecuteVisionAsync(AgentAction action, UiElement element, ActionContext context, CancellationToken cancellationToken)
    {
        var (x, y) = element.Bounds.Center;
        if (!await EnsureForegroundAsync(context.TargetWindow, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
        context.Gate.ThrowIfClosed();
        switch (action.Kind)
        {
            case ActionKind.SetValue:
                _input.Click(x, y);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                context.Gate.ThrowIfClosed();
                _input.Press("ctrl+a");
                await _input.TypeTextAsync(action.Value ?? "", element.IsMultiline, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok("", "Vision + SendInput");
            case ActionKind.SetChecked when element.IsChecked == (action.Checked ?? true):
                return ActionResult.Ok("bereits gesetzt", "Vision");
            case ActionKind.SelectOption:
                _input.Click(x, y);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                context.Gate.ThrowIfClosed();
                await _input.TypeTextAsync(action.Option ?? action.Value ?? "", false, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
                _input.Press("enter");
                return ActionResult.Ok("", "Vision + SendInput", structureChanged: true);
            default:
                _input.Click(x, y);
                return ActionResult.Ok("", "Vision-Mausklick", structureChanged: true);
        }
    }

    // ------------------------------------------------------------------ system actions
    private async Task<ActionResult> ExecuteSystemAsync(AgentAction action, ActionContext context, CancellationToken cancellationToken)
    {
        var target = context.TargetWindow;
        switch (action.Kind)
        {
            case ActionKind.TypeText:
            {
                if (!await EnsureForegroundAsync(target, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
                await _input.TypeTextAsync(action.Value ?? "", allowNewlines: true, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok("Text eingegeben.", "SendInput");
            }
            case ActionKind.Hotkey:
            {
                var chord = KeyChord.Parse(action.Keys ?? "");
                if (!await EnsureForegroundAsync(target, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
                for (var i = 0; i < 20 && InputSimulator.UserHoldsModifier(); i++) { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
                context.Gate.ThrowIfClosed();
                _input.PressChord(chord);
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                var foreground = _windows.GetForegroundWindow();
                return ActionResult.Ok($"{action.Keys} gedrückt.", "SendInput", structureChanged: true) with
                {
                    NewTargetWindow = foreground is not null && foreground.Handle != target.Handle && foreground.ProcessId != Environment.ProcessId ? foreground : null,
                };
            }
            case ActionKind.MouseClick:
            {
                if (action.X is not { } x || action.Y is not { } y) { return ActionResult.Fail(ActionErrorKind.InvalidArguments, "Koordinaten fehlen."); }
                if (!await EnsureForegroundAsync(target, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
                _input.Click(target.Bounds.X + x, target.Bounds.Y + y);
                return ActionResult.Ok("Geklickt.", "Mausklick", structureChanged: true);
            }
            case ActionKind.Scroll:
            {
                var element = action.TargetId is { } id ? context.Snapshot?.Find(id) : null;
                if (context.Snapshot?.Source == PerceptionSource.BrowserDom && _dom.IsConnected(target))
                {
                    if (!await EnsureForegroundAsync(target, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
                    var center = target.Bounds.Center;
                    _input.Wheel(center.X, center.Y, (action.Direction ?? "down").StartsWith("up", StringComparison.OrdinalIgnoreCase) ? 5 : -5);
                    return ActionResult.Ok("Gescrollt.", "Mausrad", structureChanged: true);
                }
                return await _uia.ScrollAsync(element, action.Direction ?? "down", context, cancellationToken).ConfigureAwait(false);
            }
            case ActionKind.LaunchApp:
            {
                var result = await _launcher.LaunchAsync(action.App ?? action.Path ?? "", action.Value, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? ActionResult.Ok(result.Message, "ShellExecute", structureChanged: true) with { NewTargetWindow = result.Window }
                    : ActionResult.Fail(ActionErrorKind.ElementNotFound, result.Message);
            }
            case ActionKind.SwitchWindow:
            {
                var window = _windows.FindWindow(action.App ?? action.TargetLabel ?? action.Value ?? "");
                if (window is null) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"Kein Fenster „{action.App}“ gefunden."); }
                var ok = await _windows.ActivateAsync(window.Handle, cancellationToken).ConfigureAwait(false);
                return ok
                    ? ActionResult.Ok($"Zu {window.DisplayName} gewechselt.", "SetForegroundWindow", structureChanged: true) with { NewTargetWindow = window }
                    : ActionResult.Fail(ActionErrorKind.Failed, "Windows hat den Fensterwechsel verhindert.");
            }
            case ActionKind.WindowState:
            {
                var window = string.IsNullOrWhiteSpace(action.App) ? target : _windows.FindWindow(action.App) ?? target;
                return WindowService.SetState(window.Handle, action.Value ?? action.Direction ?? "")
                    ? ActionResult.Ok("Fensterzustand geändert.", "ShowWindow", structureChanged: true)
                    : ActionResult.Fail(ActionErrorKind.InvalidArguments, "Unbekannter Fensterzustand (maximize, minimize, restore).");
            }
            case ActionKind.CloseWindow:
            {
                var window = string.IsNullOrWhiteSpace(action.App) ? target : _windows.FindWindow(action.App);
                if (window is null) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, "Fenster nicht gefunden."); }
                WindowService.Close(window.Handle);
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok("Fenster geschlossen.", "WM_CLOSE", structureChanged: true) with { NewTargetWindow = _windows.GetForegroundWindow() };
            }
            case ActionKind.OpenFile:
            {
                var path = Kairo.Core.Files.FileOperations.ResolvePath(action.Path);
                if (!File.Exists(path) && !Directory.Exists(path)) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"„{path}“ existiert nicht."); }
                var before = _windows.ListWindows().Select(w => w.Handle).ToHashSet();
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                var window = await WaitForNewWindowAsync(before, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok($"„{Path.GetFileName(path)}“ geöffnet.", "ShellExecute", structureChanged: true) with { NewTargetWindow = window };
            }
            case ActionKind.OpenUrl:
            {
                var url = action.Url ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { return ActionResult.Fail(ActionErrorKind.InvalidArguments, "Ungültige Adresse."); }
                if (_dom.IsConnected(target) && uri.Scheme is "http" or "https")
                {
                    return await _dom.TabOperationAsync(target, "new", url, null, cancellationToken).ConfigureAwait(false);
                }
                var before = _windows.ListWindows().Select(w => w.Handle).ToHashSet();
                using (Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { }
                var window = await WaitForNewWindowAsync(before, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok("Adresse geöffnet.", "ShellExecute", structureChanged: true) with { NewTargetWindow = window ?? _windows.GetForegroundWindow() };
            }
            case ActionKind.BrowserTab:
                return await BrowserTabAsync(action, target, cancellationToken).ConfigureAwait(false);
            case ActionKind.ClipboardSet:
                return await _clipboard.SetTextAsync(action.Value ?? "").ConfigureAwait(false)
                    ? ActionResult.Ok("In die Zwischenablage kopiert.", "Clipboard")
                    : ActionResult.Fail(ActionErrorKind.Failed, "Die Zwischenablage ist blockiert.");
            case ActionKind.ClipboardGet:
            {
                var text = await _clipboard.GetTextAsync().ConfigureAwait(false);
                return new ActionResult { Success = true, Message = text is null ? "Zwischenablage enthält keinen Text." : "Zwischenablage gelesen.", Data = text ?? "", Strategy = "Clipboard" };
            }
            default:
                return ActionResult.Fail(ActionErrorKind.NotSupported, $"Die Aktion {action.Kind} wird hier nicht unterstützt.");
        }
    }

    private async Task<ActionResult> BrowserTabAsync(AgentAction action, WindowInfo target, CancellationToken cancellationToken)
    {
        var op = (action.Value ?? action.Direction ?? "new").Trim().ToLowerInvariant();
        if (_dom.IsConnected(target))
        {
            return await _dom.TabOperationAsync(target, op, action.Url, action.TargetLabel, cancellationToken).ConfigureAwait(false);
        }

        // Keyboard fallback for any browser.
        if (!await EnsureForegroundAsync(target, cancellationToken).ConfigureAwait(false)) { return NotForeground(); }
        switch (op)
        {
            case "new":
                _input.Press("ctrl+t");
                if (!string.IsNullOrWhiteSpace(action.Url))
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    await _input.TypeTextAsync(action.Url, false, 0, () => true, cancellationToken).ConfigureAwait(false);
                    _input.Press("enter");
                }
                break;
            case "close": _input.Press("ctrl+w"); break;
            case "back": _input.Press("alt+left"); break;
            case "reload": _input.Press("f5"); break;
            case "switch": _input.Press("ctrl+tab"); break;
            case "navigate":
                _input.Press("ctrl+l");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                await _input.TypeTextAsync(action.Url ?? "", false, 0, () => true, cancellationToken).ConfigureAwait(false);
                _input.Press("enter");
                break;
            default:
                return ActionResult.Fail(ActionErrorKind.InvalidArguments, $"Unbekannte Tab-Aktion „{op}“.");
        }
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return ActionResult.Ok("", "Tastenkürzel", structureChanged: true);
    }

    /// <summary>Simulated input is only sent when the target window really is in the foreground.</summary>
    private async Task<bool> EnsureForegroundAsync(WindowInfo target, CancellationToken cancellationToken)
    {
        if (target.Handle == 0 || await _windows.ActivateAsync(target.Handle, cancellationToken).ConfigureAwait(false)) { return true; }
        _log.Warn("exec", "target window could not be activated, simulated input suppressed");
        return false;
    }

    private static ActionResult NotForeground() =>
        ActionResult.Fail(ActionErrorKind.Failed, "Das Zielfenster konnte nicht in den Vordergrund geholt werden – Kairo hat deshalb keine Tastatur- oder Mauseingabe gesendet.");

    private async Task<WindowInfo?> WaitForNewWindowAsync(HashSet<nint> before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            var fresh = _windows.ListWindows().FirstOrDefault(w => !before.Contains(w.Handle));
            if (fresh is not null) { return fresh; }
        }
        return null;
    }
}
