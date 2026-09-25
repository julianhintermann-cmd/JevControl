using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Kairo.Core.Settings;
using Microsoft.Win32;

namespace Kairo.App.Services;

/// <summary>Light/Dark theme (follows Windows when set to "System") and Mica/Acrylic backdrops via DWM.</summary>
public sealed class ThemeService
{
    private readonly List<Window> _windows = [];
    private ThemePreference _preference = ThemePreference.System;
    private bool _useMica = true;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _preference == ThemePreference.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(Apply);
            }
        };
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    /// <summary>Windows 11 22H2+ supports DWMWA_SYSTEMBACKDROP_TYPE (Mica/Acrylic).</summary>
    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Windows 11 (rounded corners).</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public void Configure(ThemePreference preference, bool useMica)
    {
        _preference = preference;
        _useMica = useMica;
        Apply();
    }

    public void Apply()
    {
        IsDark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = new Uri($"pack://application:,,,/Kairo;component/Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        dictionaries[0] = new ResourceDictionary { Source = source };
        foreach (var window in _windows.ToList()) { ApplyWindowChrome(window); }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    /// <summary>Registers a window for dark title bar + backdrop updates.</summary>
    public void Track(Window window, BackdropKind backdrop)
    {
        window.Tag = backdrop;
        if (!_windows.Contains(window)) { _windows.Add(window); }
        window.Closed += (_, _) => _windows.Remove(window);
        if (new WindowInteropHelper(window).Handle != 0) { ApplyWindowChrome(window); }
        else { window.SourceInitialized += (_, _) => ApplyWindowChrome(window); }
    }

    private void ApplyWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) { return; }
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (IsWindows11)
        {
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        var kind = window.Tag is BackdropKind k ? k : BackdropKind.None;
        var useBackdrop = _useMica && SupportsBackdrop && kind != BackdropKind.None;
        var type = useBackdrop ? (int)kind : DWMSBT_NONE;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = useBackdrop ? Colors.Transparent : ((SolidColorBrush)Application.Current.Resources["Kairo.Window.Background"]).Color;
        }
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        window.Background = useBackdrop
            ? Brushes.Transparent
            : kind == BackdropKind.Acrylic
                ? (Brush)Application.Current.Resources["Kairo.Overlay.Background"]
                : (Brush)Application.Current.Resources["Kairo.Window.Background"];
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_NONE = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>DWM system backdrop types (values of DWM_SYSTEMBACKDROP_TYPE).</summary>
public enum BackdropKind
{
    None = 1,
    Mica = 2,
    Acrylic = 3,
    MicaAlt = 4,
}
