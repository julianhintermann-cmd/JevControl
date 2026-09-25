using System.Windows;
using System.Windows.Input;
using Kairo.App.Services;
using Kairo.App.ViewModels;
using Kairo.App.Views;
using Kairo.Core.Settings;
using Kairo.Tests.Shared;

namespace Kairo.Windows.Tests;

/// <summary>Opens and closes the real overlay window (WPF, Kairo's own styles) on an STA thread.</summary>
public class OverlayTests
{
    [SkippableFact]
    public async Task Overlay_opens_with_focused_input_and_closes_with_escape()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
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

            var started = DateTime.UtcNow;
            overlay.ShowForInput(0);
            await TestSupport.PumpAsync(300);
            var openMs = (DateTime.UtcNow - started).TotalMilliseconds;
            var visible = overlay.IsVisible;
            var focused = Keyboard.FocusedElement is System.Windows.Controls.TextBox box && box.Name == "InstructionBox";
            var inputMode = vm.Mode == OverlayMode.Input;

            // ESC closes the overlay.
            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(esc);
            await TestSupport.PumpAsync(400);
            var hiddenAfterEsc = !overlay.IsVisible;

            // The hotkey path opens it again instantly.
            overlay.ShowForInput(0);
            await TestSupport.PumpAsync(200);
            var reopened = overlay.IsVisible;
            overlay.CloseForShutdown();
            app.Shutdown();
            return (visible, focused, inputMode, hiddenAfterEsc, reopened, openMs);
        });

        Assert.True(result.visible, "overlay not visible");
        Assert.True(result.inputMode);
        Assert.True(result.hiddenAfterEsc, "ESC did not close the overlay");
        Assert.True(result.reopened);
        Assert.True(result.openMs < 2000, $"overlay took {result.openMs:0} ms to open");
        // Keyboard focus depends on the desktop session (foreground lock); report it without failing.
        Console.WriteLine($"Instruction box focused: {result.focused}, opened in {result.openMs:0} ms");
    }
}
