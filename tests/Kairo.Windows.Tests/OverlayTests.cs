using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Kairo.App.Services;
using Kairo.App.ViewModels;
using Kairo.App.Views;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Tests.Shared;
using Kairo.Windows.Hotkeys;
using Kairo.Windows.Input;
using Kairo.Windows.Windows;

namespace Kairo.Windows.Tests;

/// <summary>
/// The real overlay window (WPF, Kairo's own styles) opened exactly like the app does it: a global hotkey
/// (Ctrl+Alt+K) pressed with real keyboard input, WM_HOTKEY → ShowForInput. WM_HOTKEY also grants the
/// foreground right that lets the overlay take the keyboard focus.
/// </summary>
public class OverlayTests
{
    [SkippableFact]
    public async Task Ctrl_alt_k_opens_the_overlay_with_focused_input_and_escape_closes_it()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession(), "Needs an interactive desktop for global hotkeys and keyboard input.");

        var result = await TestSupport.RunStaAsync(async dispatcher =>
        {
            var app = new Kairo.App.App();
            app.InitializeComponent(); // loads Kairo's theme and styles
            var theme = new ThemeService();
            theme.Configure(ThemePreference.Light, useMica: true);
            var settings = new KairoSettings();
            var vm = new OverlayViewModel();
            var overlay = new OverlayWindow(vm, theme, () => settings);
            overlay.Prepare();
            var windows = new WindowService(KairoLogger.Null) { IncludeOwnWindows = true };
            var diagnostics = new List<string>();
            overlay.Deactivated += (_, _) =>
            {
                var fg = windows.GetForegroundWindow();
                diagnostics.Add($"deactivated → foreground: '{fg?.Title}' ({fg?.ProcessName})");
            };

            using var hotkeys = new GlobalHotkeyManager(KairoLogger.Null);
            var binding = HotkeyBinding.DefaultOverlay;
            var chord = "ctrl+alt+k";
            if (hotkeys.Register(binding, () => { }, out var probeId) != HotkeyRegistrationResult.Registered)
            {
                // Ctrl+Alt+K is taken on this machine (conflict detection is tested separately) – use a free combination.
                binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F10");
                chord = "ctrl+alt+shift+f10";
                diagnostics.Add("Ctrl+Alt+K is registered by another application; using Ctrl+Alt+Shift+F10.");
            }
            else
            {
                hotkeys.Unregister(probeId);
            }

            var stopwatch = new Stopwatch();
            TaskCompletionSource<double>? opened = null;
            Assert.Equal(HotkeyRegistrationResult.Registered, hotkeys.Register(binding, () => dispatcher.BeginInvoke(() =>
            {
                overlay.ShowForInput(windows.GetForegroundWindow()?.Handle ?? 0);
                opened?.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
            }), out _));

            async Task<double?> PressHotkeyAsync()
            {
                opened = new TaskCompletionSource<double>();
                stopwatch.Restart();
                new InputSimulator().Press(chord);
                var winner = await Task.WhenAny(opened.Task, Task.Delay(5000));
                return winner == opened.Task ? opened.Task.Result : null;
            }

            var openMs = await PressHotkeyAsync();
            await TestSupport.PumpAsync(300);
            var visible = overlay.IsVisible;
            var inputMode = vm.Mode == OverlayMode.Input;
            var focused = Keyboard.FocusedElement is System.Windows.Controls.TextBox { Name: "InstructionBox" };
            var box = (System.Windows.Controls.TextBox)overlay.FindName("InstructionBox");

            // ENTER (without Shift) is intercepted and starts the task instead of adding a line break.
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(enter);
            var enterHandled = enter.Handled;

            // Real keystrokes: SHIFT+ENTER inserts a line break, ENTER does not.
            string? typed = null;
            if (focused)
            {
                var input = new InputSimulator();
                await input.TypeTextAsync("a", false, 0, () => true, CancellationToken.None);
                input.Press("shift+enter");
                await input.TypeTextAsync("b", false, 0, () => true, CancellationToken.None);
                input.Press("enter");
                await TestSupport.PumpAsync(300);
                typed = box.Text;
            }

            // ESC closes the overlay (real key press when it has the focus, routed event otherwise).
            if (focused)
            {
                new InputSimulator().Press("esc");
            }
            else
            {
                var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                overlay.RaiseEvent(esc);
            }
            await TestSupport.PumpAsync(400);
            var hiddenAfterEsc = !overlay.IsVisible;

            // Pressing the hotkey again opens it again.
            var reopenMs = await PressHotkeyAsync();
            await TestSupport.PumpAsync(200);
            var reopened = overlay.IsVisible;

            overlay.CloseForShutdown();
            app.Shutdown();
            return (openMs, visible, inputMode, focused, enterHandled, typed, hiddenAfterEsc, reopenMs, reopened, Diagnostics: string.Join(" | ", diagnostics));
        });

        Console.WriteLine($"Overlay opened {result.openMs:0} ms after the key press, input focused: {result.focused}, reopened after {result.reopenMs:0} ms. {result.Diagnostics}");
        Assert.True(result.openMs is not null, "The hotkey did not open the overlay. " + result.Diagnostics);
        Assert.True(result.visible, "Overlay not visible after opening. " + result.Diagnostics);
        Assert.True(result.inputMode);
        Assert.True(result.focused, "The instruction box did not get the keyboard focus. " + result.Diagnostics);
        Assert.True(result.enterHandled, "ENTER was not intercepted.");
        Assert.Equal("a\nb", result.typed?.Replace("\r\n", "\n"));
        Assert.True(result.hiddenAfterEsc, "ESC did not close the overlay. " + result.Diagnostics);
        Assert.True(result.reopenMs is not null && result.reopened, "The hotkey did not reopen the overlay. " + result.Diagnostics);
        Assert.True(result.openMs < 1000, $"Overlay took {result.openMs:0} ms to open.");
    }
}
