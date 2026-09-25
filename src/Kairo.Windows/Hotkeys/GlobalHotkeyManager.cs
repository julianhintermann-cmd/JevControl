using System.Windows.Interop;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Windows.Input;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Hotkeys;

public enum HotkeyRegistrationResult
{
    Registered,
    AlreadyInUse,
    Invalid,
    Failed,
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey. Needs a window with a message loop (the app's hidden
/// message window created on the WPF UI thread). Detects combinations already taken by other apps.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly KairoLogger _log;
    private readonly Dictionary<int, (HotkeyBinding Binding, Action Callback)> _registered = [];
    private int _nextId = 0x4B00;

    public GlobalHotkeyManager(KairoLogger log)
    {
        _log = log;
        // A hidden top-level window (not message-only): the installer's CloseApplication can then close Kairo gracefully.
        var parameters = new HwndSourceParameters("KairoHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public nint Handle => _source.Handle;

    /// <summary>Raised when the window receives WM_CLOSE (e.g. from the installer).</summary>
    public event EventHandler? CloseRequested;

    public static bool TryConvert(HotkeyBinding binding, out uint modifiers, out uint vk)
    {
        modifiers = MOD_NOREPEAT;
        vk = 0;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) { modifiers |= MOD_ALT; }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) { modifiers |= MOD_CONTROL; }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) { modifiers |= MOD_SHIFT; }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) { modifiers |= MOD_WIN; }
        if (binding.Modifiers == HotkeyModifiers.None) { return false; }
        try
        {
            vk = KeyChord.Parse(binding.Key).Key;
            return vk != 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public HotkeyRegistrationResult Register(HotkeyBinding binding, Action callback, out int id)
    {
        id = 0;
        if (!TryConvert(binding, out var modifiers, out var vk)) { return HotkeyRegistrationResult.Invalid; }
        id = _nextId++;
        if (RegisterHotKey(_source.Handle, id, modifiers, vk))
        {
            _registered[id] = (binding, callback);
            _log.Info("hotkey", $"registered {binding}");
            return HotkeyRegistrationResult.Registered;
        }

        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        _log.Warn("hotkey", $"registration of {binding} failed (Win32 error {error})");
        return error == ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyRegistrationResult.AlreadyInUse : HotkeyRegistrationResult.Failed;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) { UnregisterHotKey(_source.Handle, id); }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList()) { Unregister(id); }
    }

    /// <summary>Checks whether a combination is free by registering it briefly.</summary>
    public HotkeyRegistrationResult Probe(HotkeyBinding binding)
    {
        if (_registered.Values.Any(r => r.Binding == binding)) { return HotkeyRegistrationResult.Registered; }
        var result = Register(binding, () => { }, out var id);
        if (result == HotkeyRegistrationResult.Registered) { Unregister(id); }
        return result;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue((int)wParam, out var entry))
        {
            handled = true;
            try { entry.Callback(); }
            catch (Exception ex) { _log.Error("hotkey", "hotkey handler failed", ex); }
        }
        else if (msg == WM_CLOSE)
        {
            handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
