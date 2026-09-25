using System.Diagnostics;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Windows;

/// <summary>Top-level window enumeration and reliable activation (Win32).</summary>
public sealed class WindowService : IWindowService
{
    private readonly KairoLogger _log;
    private readonly int _ownProcessId = Environment.ProcessId;

    public WindowService(KairoLogger log) => _log = log;

    /// <summary>Windows of Kairo itself (overlay, settings) are never returned as targets.</summary>
    public bool IncludeOwnWindows { get; set; }

    public WindowInfo? GetForegroundWindow()
    {
        var hwnd = Interop.NativeMethods.GetForegroundWindow();
        return hwnd == 0 ? null : Describe(hwnd);
    }

    public WindowInfo? GetWindow(nint handle) => IsWindow(handle) ? Describe(handle) : null;

    public bool IsWindowAlive(nint handle) => handle != 0 && IsWindow(handle);

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var handles = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd)) { handles.Add(hwnd); }
            return true;
        }, 0);

        var result = new List<WindowInfo>(handles.Count);
        foreach (var h in handles)
        {
            var info = Describe(h);
            if (info is not null && (IncludeOwnWindows || info.ProcessId != _ownProcessId)) { result.Add(info); }
        }
        return result;
    }

    /// <summary>Visible, non-tool, non-cloaked top-level windows with a title (what Alt+Tab shows).</summary>
    private static bool IsCandidate(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) { return false; }
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) { return false; }
        if (Interop.NativeMethods.GetWindow(hwnd, GW_OWNER) != 0 && (ex & WS_EX_APPWINDOW) == 0) { return false; }
        if (GetWindowTextLength(hwnd) == 0) { return false; }
        var cls = GetWindowClass(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    public static WindowInfo? Describe(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) { return null; }
        GetWindowThreadProcessId(hwnd, out var pid);
        string processName;
        string? path = null;
        try
        {
            path = GetProcessPath(pid);
            processName = path is not null ? Path.GetFileNameWithoutExtension(path) : Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            processName = "";
        }

        var rect = GetVisibleBounds(hwnd);
        return new WindowInfo
        {
            Handle = hwnd,
            Title = GetWindowTitle(hwnd),
            ProcessName = processName,
            ProcessId = pid,
            ExecutablePath = path,
            ClassName = GetWindowClass(hwnd),
            Bounds = new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height),
            IsMinimized = IsIconic(hwnd),
        };
    }

    public async Task<bool> ActivateAsync(nint handle, CancellationToken cancellationToken)
    {
        if (!IsWindow(handle)) { return false; }
        if (Interop.NativeMethods.GetForegroundWindow() == handle) { return true; }

        if (IsIconic(handle)) { ShowWindow(handle, SW_RESTORE); }
        ForceForeground(handle);

        // Activation is asynchronous – wait briefly until the window really is in front.
        for (var i = 0; i < 20; i++)
        {
            if (Interop.NativeMethods.GetForegroundWindow() == handle) { return true; }
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            if (i == 6) { ForceForeground(handle); }
        }
        _log.Warn("windows", "window activation not confirmed");
        return Interop.NativeMethods.GetForegroundWindow() == handle;
    }

    /// <summary>
    /// SetForegroundWindow is restricted by Windows (foreground lock). Kairo is allowed to change the
    /// foreground while it holds it (overlay) or right after its hotkey; otherwise the thread-input trick is used.
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        if (SetForegroundWindow(hwnd)) { return; }

        var foreground = Interop.NativeMethods.GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            }
            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }
            BringWindowToTop(hwnd);
            ShowWindow(hwnd, IsZoomed(hwnd) ? SW_MAXIMIZE : SW_SHOW);
            if (!SetForegroundWindow(hwnd))
            {
                // Last resort: a synthetic ALT press lifts the foreground lock for this process.
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = VK_MENU;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = VK_MENU;
                inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
                SetForegroundWindow(hwnd);
            }
        }
        finally
        {
            if (attachedForeground) { AttachThreadInput(currentThread, foregroundThread, false); }
            if (attachedTarget) { AttachThreadInput(currentThread, targetThread, false); }
        }
    }

    public static bool SetState(nint hwnd, string state) => state.ToLowerInvariant() switch
    {
        "maximize" or "maximieren" => ShowWindow(hwnd, SW_MAXIMIZE),
        "minimize" or "minimieren" => ShowWindow(hwnd, SW_MINIMIZE),
        "restore" or "wiederherstellen" or "normal" => ShowWindow(hwnd, SW_RESTORE),
        _ => false,
    };

    public static bool Close(nint hwnd) => PostMessage(hwnd, WM_CLOSE, 0, 0);

    /// <summary>Finds an open window by title or process name (fuzzy).</summary>
    public WindowInfo? FindWindow(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { return null; }
        var q = query.Trim();
        var windows = ListWindows();
        return windows.FirstOrDefault(w => string.Equals(w.ProcessName, q, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => w.Title.Equals(q, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => w.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase))
               ?? windows
                   .Select(w => (w, Score: Kairo.Core.Perception.ElementMatcher.TextSimilarity(
                       Kairo.Core.Perception.ElementMatcher.Normalize(q),
                       Kairo.Core.Perception.ElementMatcher.Normalize(w.Title + " " + w.ProcessName))))
                   .Where(x => x.Score >= 0.5)
                   .OrderByDescending(x => x.Score)
                   .Select(x => x.w)
                   .FirstOrDefault();
    }
}
