using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kairo.App.Services;
using Kairo.App.ViewModels;
using Kairo.Core.Settings;

namespace Kairo.App.Views;

/// <summary>
/// The Kairo command palette. Opens instantly (pre-created, only shown/hidden), turns into a compact,
/// non-activating progress bar while Kairo controls the computer and asks for approvals inline.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double FullWidth = 720;
    private const double CompactWidth = 600;

    private readonly OverlayViewModel _vm;
    private readonly Func<KairoSettings> _settings;
    private readonly DispatcherTimer _autoHide = new();
    private nint _hwnd;
    private nint _anchorWindow;
    private bool _allowClose;

    public OverlayWindow(OverlayViewModel viewModel, ThemeService theme, Func<KairoSettings> settings)
    {
        InitializeComponent();
        _vm = viewModel;
        _settings = settings;
        DataContext = viewModel;
        theme.Track(this, BackdropKind.Acrylic);

        viewModel.ModeChanged += (_, mode) => OnModeChanged(mode);
        viewModel.CloseRequested += (_, _) => HideOverlay();
        viewModel.FocusRequested += (_, _) => FocusForInteraction();
        PreviewKeyDown += OnWindowKeyDown;
        Deactivated += (_, _) =>
        {
            if (_vm.Mode == OverlayMode.Input && IsVisible) { HideOverlay(); }
        };
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            if (_vm.Mode == OverlayMode.Result && _vm.ResultSuccess && !IsKeyboardFocusWithin && !IsMouseOver) { HideOverlay(); }
        };
    }

    public bool IsOpen => IsVisible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Keep the overlay out of Kairo's own screenshots (vision fallback) and screen recordings.
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)((long)ex | WS_EX_TOOLWINDOW));
    }

    /// <summary>Creates the native window without showing it, so the first hotkey press opens instantly.</summary>
    public void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
    }

    // ------------------------------------------------------------------ show / hide
    public void ShowForInput(nint anchorWindow)
    {
        _autoHide.Stop();
        _anchorWindow = anchorWindow;
        if (_vm.Mode is OverlayMode.Result) { _vm.NewTaskCommand.Execute(null); }
        SetNoActivate(false);
        Width = _vm.Mode == OverlayMode.Input ? FullWidth : CompactWidth;
        ShowAnimated();
        Position(compact: _vm.Mode is OverlayMode.Running);
        Activate();
        Kairo.Windows.Windows.WindowService.ForceForeground(_hwnd);
        FocusForInteraction();
    }

    public void HideOverlay()
    {
        if (!IsVisible) { return; }
        _autoHide.Stop();
        if (_settings().Appearance.Animations)
        {
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90));
            fade.Completed += (_, _) =>
            {
                Hide();
                Opacity = 1;
            };
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Hide();
        }
    }

    private void ShowAnimated()
    {
        BeginAnimation(OpacityProperty, null);
        if (!_settings().Appearance.Animations)
        {
            Opacity = 1;
            Show();
            return;
        }
        Opacity = 0;
        Show();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    /// <summary>Places the overlay on the monitor of the window the user works in (physical pixels).</summary>
    private void Position(bool compact)
    {
        UpdateLayout();
        var screen = _anchorWindow != 0 ? System.Windows.Forms.Screen.FromHandle(_anchorWindow) : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var area = screen.WorkingArea;
        GetWindowRect(_hwnd, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = area.Left + (area.Width - width) / 2;
        var y = compact
            ? area.Top + 12
            : _settings().Appearance.OverlayPlacement == OverlayPlacement.Center
                ? area.Top + (area.Height - height) / 2
                : area.Top + (int)(area.Height * 0.16);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnModeChanged(OverlayMode mode)
    {
        switch (mode)
        {
            case OverlayMode.Running:
                // Compact, non-activating bar at the top: the target application keeps the keyboard focus.
                Width = CompactWidth;
                var hadFocus = IsActive;
                SetNoActivate(true);
                if (!IsVisible) { ShowAnimated(); }
                Dispatcher.BeginInvoke(() => Position(compact: true), DispatcherPriority.Loaded);
                // Hand the keyboard focus back to the application Kairo is working in.
                if (hadFocus && _vm.TargetWindow is { Handle: not 0 } target)
                {
                    Kairo.Windows.Windows.WindowService.ForceForeground(target.Handle);
                }
                break;
            case OverlayMode.Approval:
            case OverlayMode.Question:
                Width = CompactWidth;
                SetNoActivate(false);
                if (!IsVisible) { ShowAnimated(); }
                Dispatcher.BeginInvoke(() => Position(compact: true), DispatcherPriority.Loaded);
                break;
            case OverlayMode.Result:
                Width = CompactWidth;
                SetNoActivate(false);
                if (!IsVisible) { ShowAnimated(); }
                Dispatcher.BeginInvoke(() => Position(compact: true), DispatcherPriority.Loaded);
                var s = _settings().General;
                if (_vm.ResultSuccess && s.AutoHideOnSuccess)
                {
                    _autoHide.Interval = TimeSpan.FromSeconds(Math.Clamp(s.AutoHideSeconds, 2, 60));
                    _autoHide.Start();
                }
                break;
            case OverlayMode.Input:
                Width = FullWidth;
                break;
        }
    }

    private void FocusForInteraction()
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (_vm.Mode)
            {
                case OverlayMode.Input:
                    InstructionBox.Focus();
                    InstructionBox.SelectAll();
                    Keyboard.Focus(InstructionBox);
                    break;
                case OverlayMode.Question:
                    Activate();
                    Kairo.Windows.Windows.WindowService.ForceForeground(_hwnd);
                    AnswerBox.Focus();
                    Keyboard.Focus(AnswerBox);
                    break;
                case OverlayMode.Approval:
                    Activate();
                    Kairo.Windows.Windows.WindowService.ForceForeground(_hwnd);
                    break;
            }
        }, DispatcherPriority.Input);
    }

    private void SetNoActivate(bool enabled)
    {
        if (_hwnd == 0) { return; }
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = enabled ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)ex);
    }

    // ------------------------------------------------------------------ keyboard
    private void OnInstructionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _vm.SubmitCommand.Execute(null);
        }
    }

    private void OnAnswerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.SendAnswerCommand.Execute(null);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) { return; }
        e.Handled = true;
        switch (_vm.Mode)
        {
            case OverlayMode.Approval:
                _vm.DenyCommand.Execute(null);
                break;
            case OverlayMode.Question:
                _vm.CancelCommand.Execute(null);
                break;
            case OverlayMode.Running:
                // Esc only hides the bar; the task continues (use "Abbrechen" to stop it).
                HideOverlay();
                break;
            default:
                HideOverlay();
                break;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideOverlay();
        }
        base.OnClosing(e);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    // ------------------------------------------------------------------ interop
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);
}
