using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairo.App.ViewModels;
using Kairo.App.Views;
using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.Models;
using Kairo.Core.Settings;
using Kairo.Windows;
using Kairo.Windows.Hotkeys;

namespace Kairo.App.Services;

/// <summary>
/// Diagnostics / documentation: renders the main UI states (overlay modes, onboarding, settings pages) in
/// light and dark theme to PNG files. Used in CI to review the UI and for the README screenshots.
/// Uses a temporary data folder – never the user's settings or API key.
/// </summary>
public static class UiScreenshots
{
    public static async Task<int> RenderAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var temp = Path.Combine(Path.GetTempPath(), "kairo-screens-" + Guid.NewGuid().ToString("N"));
        var paths = new KairoPaths(Path.Combine(temp, "roaming"), Path.Combine(temp, "local"));
        var theme = new ThemeService();
        var count = 0;
        try
        {
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                theme.Configure(preference, useMica: false);
                var suffix = preference.ToString().ToLowerInvariant();
                var vm = new OverlayViewModel();
                await using var runtime = new KairoRuntime(vm, new KairoRuntimeOptions { Paths = paths, StartBrowserBridge = false });
                vm.Attach(runtime);
                runtime.Secrets.SetSecret(KairoRuntime.ApiKeySecretName, "sk-or-v1-screenshot-demo-key-not-real");
                var settings = runtime.Settings.Current;

                // ---- overlay
                var overlay = new OverlayWindow(vm, theme, () => settings) { Left = -20000, Top = -20000, ShowActivated = false };
                vm.PrepareForInput(new WindowInfo { Handle = 0, Title = "Kontakt – Muster AG – Google Chrome", ProcessName = "chrome" }, null);
                vm.Instruction = "Fülle das Kontaktformular aus, das ich gerade geöffnet habe. Meine Kontaktinformationen findest du unter C:\\Users\\Max\\Documents\\Kontaktinformationen.pdf.";
                overlay.Show();
                count += await SaveAsync(overlay, outputDirectory, $"overlay-input-{suffix}.png");

                vm.Mode = OverlayMode.Running;
                vm.StatusText = "Fülle Formular aus …";
                vm.IsProgressIndeterminate = false;
                vm.Progress = 60;
                vm.Steps.Add(new StepItem { Text = "Datei gelesen: Kontaktinformationen.pdf (212 Zeichen, lokal verarbeitet).", Kind = TaskLogKind.Info });
                vm.Steps.Add(new StepItem { Text = "„E-Mail“ → [3] E-Mail – Jev bestätigt (97 %)", Kind = TaskLogKind.Decision });
                vm.Steps.Add(new StepItem { Text = "E-Mail eintragen [E-Mail]", Kind = TaskLogKind.Action, Success = true });
                vm.ShowDetails = true;
                count += await SaveAsync(overlay, outputDirectory, $"overlay-running-{suffix}.png");

                vm.ApprovalTitle = "Sensible Aktion freigeben?";
                vm.ApprovalDescription = "Kairo klickt auf „Absenden“ in chrome.";
                vm.ApprovalReasons = "• Schaltfläche „Absenden“ sendet oder bestätigt etwas.";
                vm.ApprovalRisk = "sensibel";
                vm.CanAllowForTask = true;
                vm.Mode = OverlayMode.Approval;
                count += await SaveAsync(overlay, outputDirectory, $"overlay-approval-{suffix}.png");

                vm.ResultSuccess = true;
                vm.ResultMessage = "Das Kontaktformular ist ausgefüllt. Es wurde nicht abgesendet.";
                vm.ResultDetails = "7 Aktionen · 1 Planungsrunde(n) · 4.8 s · 3 Modellaufrufe (2 Jev) · Kosten $0.0061";
                vm.ShowDetails = false;
                vm.Mode = OverlayMode.Result;
                count += await SaveAsync(overlay, outputDirectory, $"overlay-result-{suffix}.png");
                overlay.CloseForShutdown();

                // ---- onboarding
                var onboardingVm = new OnboardingViewModel(runtime, _ => HotkeyRegistrationResult.Registered, _ => { });
                var onboarding = new OnboardingWindow(onboardingVm, theme) { Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                onboarding.Show();
                for (var step = 0; step <= 3; step++)
                {
                    onboardingVm.Step = step;
                    if (step == 1)
                    {
                        onboardingVm.Results.Add(new CheckResult("API-Schlüssel", "Schlüssel gültig. Verbleibendes Limit: $9.42.", true));
                        onboardingVm.Results.Add(new CheckResult("Planungsmodell: anthropic/claude-sonnet-5", "Verfügbar, strukturierte Ausgabe.", true));
                        onboardingVm.Results.Add(new CheckResult("Entscheidungsmodell (Jev): ~typesafe/jev-latest", "Jev antwortet in 412 ms (typesafe/jev-1.13).", true));
                        onboardingVm.TestMessage = "Alles bereit. Kairo kann OpenRouter und Jev verwenden.";
                    }
                    count += await SaveAsync(onboarding, outputDirectory, $"onboarding-{step}-{suffix}.png");
                }
                onboarding.Close();

                // ---- settings
                var settingsVm = new SettingsViewModel(new ScreenshotHost(runtime));
                var settingsWindow = new SettingsWindow(settingsVm, theme) { Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                settingsWindow.Show();
                foreach (var page in settingsVm.Navigation)
                {
                    settingsWindow.ShowPage(page.Key);
                    count += await SaveAsync(settingsWindow, outputDirectory, $"settings-{page.Key}-{suffix}.png");
                }
                settingsWindow.Close();
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    private static async Task<int> SaveAsync(Window window, string directory, string name)
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        window.UpdateLayout();
        await Task.Delay(200);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) { return 0; }

        var root = (FrameworkElement)window.Content;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background is SolidColorBrush { Color.A: > 0 } b ? b : (Brush)Application.Current.Resources["Kairo.Window.Background"], null, new Rect(0, 0, width, height));
            // 1:1 – a default VisualBrush would stretch the visual's descendant bounds (including shadows) into the rectangle.
            var content = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = content,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = content,
            }, null, content);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
        return 1;
    }

    private sealed class ScreenshotHost(KairoRuntime runtime) : IAppHost
    {
        public KairoRuntime Runtime { get; } = runtime;
        public HotkeyRegistrationResult ProbeHotkey(HotkeyBinding binding) => HotkeyRegistrationResult.Registered;
        public (HotkeyRegistrationResult Overlay, HotkeyRegistrationResult Stop) ApplyHotkeys() => (HotkeyRegistrationResult.Registered, HotkeyRegistrationResult.Registered);
        public void SetAutostart(bool enabled) { }
        public void ApplyAppearance() { }
        public bool ControlPaused { get; set; }
        public void Shutdown() { }
    }
}
