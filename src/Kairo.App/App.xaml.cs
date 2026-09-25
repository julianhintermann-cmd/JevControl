using System.Windows;
using System.Windows.Threading;
using Kairo.App.Services;
using Kairo.App.ViewModels;
using Kairo.App.Views;
using Kairo.Core.Agent;
using Kairo.Core.Settings;
using Kairo.Windows;
using Kairo.Windows.Hotkeys;
using Kairo.Windows.Security;

namespace Kairo.App;

/// <summary>
/// Application entry: tray-resident background app. Handles the command line modes used by the installer
/// (--selftest, --uninstall-cleanup), single instance, hotkeys, overlay, onboarding and settings.
/// </summary>
public partial class App : Application, IAppHost
{
    private SingleInstance? _singleInstance;
    private KairoRuntime? _runtime;
    private GlobalHotkeyManager? _hotkeys;
    private TrayIconService? _tray;
    private ThemeService? _theme;
    private OverlayViewModel? _overlayVm;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboarding;
    private readonly AutostartManager _autostart = new();
    private int _overlayHotkeyId;
    private int _stopHotkeyId;
    private bool _shuttingDown;

    public KairoRuntime Runtime => _runtime ?? throw new InvalidOperationException("Runtime not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            var outIndex = Array.FindIndex(args, a => a.Equals("--selftest-out", StringComparison.OrdinalIgnoreCase));
            var output = outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null;
            Shutdown(CommandLineActions.RunSelfTest(output));
            return;
        }

        var screenshotIndex = Array.FindIndex(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (screenshotIndex >= 0)
        {
            var dir = screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : Path.Combine(Environment.CurrentDirectory, "screenshots");
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var rendered = await UiScreenshots.RenderAsync(dir);
                    Shutdown(rendered > 0 ? 0 : 1);
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                    Shutdown(2);
                }
            });
            return;
        }

        if (args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
        {
            var code = CommandLineActions.RunUninstallCleanup(
                removeData: args.Contains("--remove-data", StringComparer.OrdinalIgnoreCase),
                quiet: args.Contains("--quiet", StringComparer.OrdinalIgnoreCase));
            Shutdown(code);
            return;
        }

        _singleInstance = SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(() => ShowOverlay()));
        if (_singleInstance is null)
        {
            // Kairo already runs: the other instance opens its overlay.
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            _runtime?.Log.Error("app", "unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        try
        {
            StartApplication(background: args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Kairo konnte nicht gestartet werden:\n\n{Kairo.Core.Telemetry.Redactor.Redact(ex.Message)}", "Kairo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartApplication(bool background)
    {
        _overlayVm = new OverlayViewModel();
        var hostPath = Path.Combine(AppContext.BaseDirectory, "Kairo.BrowserHost.exe");
        _runtime = new KairoRuntime(_overlayVm, new KairoRuntimeOptions
        {
            BrowserHostPath = File.Exists(hostPath) ? hostPath : null,
        });
        _overlayVm.Attach(_runtime);

        _theme = new ThemeService();
        var settings = _runtime.Settings.Current;
        _theme.Configure(settings.Appearance.Theme, settings.Appearance.UseMica);

        _overlay = new OverlayWindow(_overlayVm, _theme, () => Runtime.Settings.Current);
        _overlay.Prepare();
        _overlayVm.SettingsRequested += (_, _) => ShowSettings();

        _hotkeys = new GlobalHotkeyManager(_runtime.Log);
        _hotkeys.CloseRequested += (_, _) => Shutdown();
        var (overlayResult, _) = ApplyHotkeys();

        _tray = new TrayIconService(() => Runtime.Tasks.Recent);
        _tray.HotkeyText = settings.Hotkeys.Overlay.ToString();
        _tray.SetPaused(false);
        _tray.OpenOverlayRequested += (_, _) => ShowOverlay();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.PauseToggled += (_, paused) => ControlPaused = paused;
        _tray.TaskSelected += (_, task) => ShowTaskResult(task);
        _tray.BalloonClicked += (_, _) => ShowOverlay();

        _runtime.ControlSwitch.PausedChanged += (_, paused) => Dispatcher.BeginInvoke(() => _tray?.SetPaused(paused));
        _runtime.Tasks.TaskStarted += (_, task) => Dispatcher.BeginInvoke(() => _tray?.SetBusy(task.StatusText));
        _runtime.Tasks.TaskFinished += (_, task) => Dispatcher.BeginInvoke(() => OnTaskFinished(task));

        EnsureNativeHostRegistration(hostPath);
        SyncAutostart(settings.General.StartWithWindows);

        if (!settings.OnboardingCompleted || !_runtime.HasApiKey)
        {
            if (background)
            {
                _tray.ShowNotification("Kairo einrichten", "Klicke hier, um deinen OpenRouter-API-Schlüssel zu hinterlegen.");
                _tray.BalloonClicked += (_, _) => ShowOnboarding();
            }
            else
            {
                ShowOnboarding();
            }
        }
        else if (!background)
        {
            _tray.ShowNotification("Kairo ist bereit", $"Drücke {settings.Hotkeys.Overlay}, um eine Aufgabe zu starten.");
        }

        if (overlayResult == HotkeyRegistrationResult.AlreadyInUse)
        {
            _tray.ShowNotification("Tastenkombination belegt",
                $"{settings.Hotkeys.Overlay} wird bereits von einer anderen Anwendung verwendet. Wähle in den Einstellungen eine andere Kombination.", error: true);
        }

        // Warm up the TLS connection to OpenRouter in the background (reduces first-request latency).
        _ = _runtime.Http.WarmUpAsync(CancellationToken.None);
    }

    private void EnsureNativeHostRegistration(string hostPath)
    {
        try
        {
            if (File.Exists(hostPath) && !NativeHostRegistrar.IsRegistered())
            {
                NativeHostRegistrar.Register(hostPath, AppContext.BaseDirectory);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _runtime?.Log.Warn("app", $"native host registration failed: {ex.GetType().Name}");
        }
    }

    private void SyncAutostart(bool enabled)
    {
        try
        {
            if (_autostart.IsEnabled() != enabled) { SetAutostart(enabled); }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _runtime?.Log.Warn("app", $"autostart sync failed: {ex.GetType().Name}");
        }
    }

    // ------------------------------------------------------------------ overlay
    private void ShowOverlay()
    {
        if (_overlay is null || _overlayVm is null || _runtime is null) { return; }

        if (_overlay.IsOpen && _overlayVm.Mode == OverlayMode.Input)
        {
            _overlay.HideOverlay();
            return;
        }

        // The window the user works in right now (before the overlay takes the focus).
        var target = _runtime.Windows.GetForegroundWindow();
        if (target is not null && target.ProcessId == Environment.ProcessId) { target = _overlayVm.TargetWindow; }

        if (!_overlayVm.IsBusy)
        {
            _overlayVm.PrepareForInput(target, IconHelper.FromExecutable(target?.ExecutablePath));
            if (target is not null && _runtime.Settings.Current.Control.PrefetchOnOverlayOpen && _runtime.HasApiKey)
            {
                // Speculative perception while the user types: local only, nothing is sent anywhere.
                _runtime.Perception.Prefetch(target);
            }
            _ = _runtime.Http.WarmUpAsync(CancellationToken.None);
        }
        _overlay.ShowForInput(target?.Handle ?? 0);
    }

    private void EmergencyStop()
    {
        if (_runtime?.Tasks.IsBusy != true) { return; }
        _runtime.Tasks.CancelCurrent("Mit der Nothalt-Tastenkombination gestoppt.");
        _tray?.ShowNotification("Kairo gestoppt", "Die laufende Aufgabe wurde abgebrochen.");
    }

    private void OnTaskFinished(AgentTask task)
    {
        _tray?.SetBusy(null);
        var settings = Runtime.Settings.Current;
        if (settings.General.ShowTrayNotifications && _overlay is { IsOpen: false })
        {
            var ok = task.State == AgentTaskState.Completed;
            _tray?.ShowNotification(ok ? "Aufgabe erledigt" : task.State.ToGerman(), (ok ? task.ResultMessage : task.ErrorMessage) ?? "", error: !ok);
        }
    }

    private void ShowTaskResult(AgentTask task)
    {
        var text = $"{task.Instruction}\n\n{task.State.ToGerman()}: {task.ResultMessage ?? task.ErrorMessage}\n\n" +
                   string.Join("\n", task.Log.TakeLast(15).Select(l => $"{l.Time:HH:mm:ss}  {l.Text}"));
        MessageBox.Show(text, "Kairo – Aufgabe", MessageBoxButton.OK, task.State == AgentTaskState.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------------ windows
    private void ShowSettings()
    {
        if (_runtime is null || _theme is null) { return; }
        _overlay?.HideOverlay();
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(new SettingsViewModel(this), _theme);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowOnboarding()
    {
        if (_runtime is null || _theme is null || _hotkeys is null) { return; }
        if (_onboarding is { IsLoaded: true })
        {
            _onboarding.Activate();
            return;
        }
        var vm = new OnboardingViewModel(_runtime, ProbeHotkey, SetAutostart);
        _onboarding = new OnboardingWindow(vm, _theme);
        _onboarding.Closed += (_, _) =>
        {
            _onboarding = null;
            if (Runtime.HasApiKey) { _tray?.ShowNotification("Kairo ist bereit", $"Drücke {Runtime.Settings.Current.Hotkeys.Overlay}, um zu starten."); }
        };
        _onboarding.Show();
        _onboarding.Activate();
    }

    // ------------------------------------------------------------------ IAppHost
    public HotkeyRegistrationResult ProbeHotkey(HotkeyBinding binding)
    {
        if (_hotkeys is null) { return HotkeyRegistrationResult.Failed; }
        var current = Runtime.Settings.Current.Hotkeys.Overlay;
        return binding == current && _overlayHotkeyId != 0 ? HotkeyRegistrationResult.Registered : _hotkeys.Probe(binding);
    }

    public (HotkeyRegistrationResult Overlay, HotkeyRegistrationResult Stop) ApplyHotkeys()
    {
        if (_hotkeys is null || _runtime is null) { return (HotkeyRegistrationResult.Failed, HotkeyRegistrationResult.Failed); }
        _hotkeys.UnregisterAll();
        _overlayHotkeyId = 0;
        _stopHotkeyId = 0;
        var hotkeys = _runtime.Settings.Current.Hotkeys;
        var overlay = _hotkeys.Register(hotkeys.Overlay, () => Dispatcher.BeginInvoke(() => ShowOverlay()), out var overlayId);
        if (overlay == HotkeyRegistrationResult.Registered) { _overlayHotkeyId = overlayId; }
        var stop = _hotkeys.Register(hotkeys.EmergencyStop, () => Dispatcher.BeginInvoke(EmergencyStop), out var stopId);
        if (stop == HotkeyRegistrationResult.Registered) { _stopHotkeyId = stopId; }
        if (_tray is not null) { _tray.HotkeyText = hotkeys.Overlay.ToString(); }
        if (_overlayVm is not null) { _overlayVm.HotkeyText = hotkeys.Overlay.ToString(); }
        return (overlay, stop);
    }

    public void SetAutostart(bool enabled)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Kairo.exe");
        _autostart.SetEnabled(enabled, exe);
    }

    public void ApplyAppearance()
    {
        var s = Runtime.Settings.Current.Appearance;
        _theme?.Configure(s.Theme, s.UseMica);
    }

    public bool ControlPaused
    {
        get => _runtime?.ControlSwitch.IsPaused == true;
        set
        {
            if (_runtime is null) { return; }
            _runtime.ControlSwitch.SetPaused(value);
            if (value) { _runtime.Tasks.CancelCurrent("Computersteuerung pausiert."); }
            _tray?.SetPaused(value);
        }
    }

    void IAppHost.Shutdown() => Shutdown();

    // ------------------------------------------------------------------ lifecycle
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _runtime?.Log.Error("app", "unhandled UI exception", e.Exception);
        e.Handled = true;
        if (_shuttingDown) { return; }
        MessageBox.Show($"Ein unerwarteter Fehler ist aufgetreten:\n\n{Kairo.Core.Telemetry.Redactor.Redact(e.Exception.Message)}", "Kairo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        try
        {
            _runtime?.Tasks.CancelCurrent("Kairo wird beendet.");
            _overlay?.CloseForShutdown();
            _tray?.Dispose();
            _hotkeys?.Dispose();
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Never block shutdown.
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
