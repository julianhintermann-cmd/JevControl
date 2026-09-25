using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairo.Core.Abstractions;
using Kairo.Core.AI.Jev;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Windows;
using Kairo.Windows.Hotkeys;
using Kairo.Windows.Security;

namespace Kairo.App.Services;

/// <summary>Extracts the icon of the application Kairo is working in (shown in the overlay).</summary>
public static class IconHelper
{
    public static ImageSource? FromExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return null; }
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) { return null; }
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}

/// <summary>Single instance per user: a second start asks the running instance to open the overlay.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, Action onSignal)
    {
        _mutex = mutex;
        _signal = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static SingleInstance? TryAcquire(Action onSecondInstance)
    {
        var id = Environment.UserName.ToLowerInvariant().GetHashCode(StringComparison.Ordinal).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        var mutex = new Mutex(true, $@"Local\Kairo-SingleInstance-{id}", out var created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Kairo-Show-{id}");
        if (!created)
        {
            signal.Set();
            signal.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, signal, onSecondInstance);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _signal.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

/// <summary>Tests an API key before it is stored (temporary clients with the candidate key).</summary>
public static class KeyTester
{
    public static async Task<ConnectionReport> TestAsync(string apiKey, string planner, string decision, string? vision, CancellationToken cancellationToken)
    {
        var log = KairoLogger.Null;
        using var http = new OpenRouterHttp(new OpenRouterOptions(), () => apiKey, log);
        var usage = new UsageTracker(log);
        var tester = new ConnectionTester(new OpenRouterClient(http, usage, log), new JevClient(http, usage, log));
        return await tester.TestAsync(planner, decision, vision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Picks the first available model of a preference list when the configured one is unavailable.</summary>
    public static string? Suggest(ConnectionReport report, string role) =>
        report.Models.FirstOrDefault(m => m.Role == role && !m.Available)?.Suggestion;
}

/// <summary>Command line modes used by the installer and CI: --selftest and --uninstall-cleanup.</summary>
public static class CommandLineActions
{
    /// <summary>Headless self test (exit code 0 = all critical checks passed). Writes a JSON report.</summary>
    public static int RunSelfTest(string? outputPath)
    {
        var report = new JsonObject { ["version"] = typeof(App).Assembly.GetName().Version?.ToString(), ["time"] = DateTimeOffset.Now.ToString("O") };
        var checks = new JsonArray();
        var critical = true;

        void Check(string name, bool isCritical, Func<string> test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var detail = test();
                checks.Add(new JsonObject { ["name"] = name, ["ok"] = true, ["detail"] = detail, ["ms"] = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                if (isCritical) { critical = false; }
                checks.Add(new JsonObject { ["name"] = name, ["ok"] = false, ["critical"] = isCritical, ["detail"] = $"{ex.GetType().Name}: {ex.Message}", ["ms"] = sw.ElapsedMilliseconds });
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), "kairo-selftest-" + Guid.NewGuid().ToString("N"));
        var paths = new KairoPaths(Path.Combine(temp, "roaming"), Path.Combine(temp, "local"));
        try
        {
            Check("settings", true, () =>
            {
                var store = new SettingsStore(paths, KairoLogger.Null);
                store.Update(s => s.Models.PlannerModel = "selftest/model");
                return new SettingsStore(paths, KairoLogger.Null).Current.Models.PlannerModel == "selftest/model" ? "ok" : throw new InvalidOperationException("roundtrip failed");
            });
            Check("dpapi", true, () =>
            {
                var secrets = new DpapiSecretStore(paths.SecretsDirectory, KairoLogger.Null);
                secrets.SetSecret("selftest", "sk-or-v1-selftest-value");
                var raw = File.ReadAllBytes(Path.Combine(paths.SecretsDirectory, "selftest.dpapi"));
                if (System.Text.Encoding.UTF8.GetString(raw).Contains("selftest-value", StringComparison.Ordinal)) { throw new InvalidOperationException("secret stored in plain text"); }
                var back = secrets.GetSecret("selftest");
                secrets.DeleteSecret("selftest");
                return back == "sk-or-v1-selftest-value" && !secrets.HasSecret("selftest") ? "ok" : throw new InvalidOperationException("roundtrip failed");
            });
            Check("runtime", true, () =>
            {
                var runtime = new KairoRuntime(new NullInteraction(), new KairoRuntimeOptions { Paths = paths, StartBrowserBridge = false });
                var windows = runtime.Windows.ListWindows().Count;
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return $"windows={windows}";
            });
            Check("hotkey", false, () =>
            {
                using var hotkeys = new GlobalHotkeyManager(KairoLogger.Null);
                var result = hotkeys.Probe(HotkeyBinding.DefaultOverlay);
                return result == HotkeyRegistrationResult.Registered ? "Strg+Alt+K frei" : throw new InvalidOperationException(result.ToString());
            });
            Check("uiautomation", false, () =>
            {
                var core = new Kairo.Windows.Automation.UiaCore(KairoLogger.Null);
                var root = Task.Run(() => core.Automation.GetRootElement().CurrentName).GetAwaiter().GetResult();
                return $"root='{root}'";
            });
            Check("native-host", false, () =>
            {
                var dir = AppContext.BaseDirectory;
                var host = Path.Combine(dir, "Kairo.BrowserHost.exe");
                var manifest = Path.Combine(dir, "com.kairo.bridge.json");
                if (!File.Exists(host)) { throw new FileNotFoundException("Kairo.BrowserHost.exe fehlt"); }
                if (!File.Exists(manifest)) { throw new FileNotFoundException("com.kairo.bridge.json fehlt"); }
                return NativeHostRegistrar.IsRegistered() ? "registriert" : "nicht registriert";
            });
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        report["checks"] = checks;
        report["ok"] = critical;
        var json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(outputPath)) { File.WriteAllText(outputPath, json); }
        Console.WriteLine(json);
        return critical ? 0 : 1;
    }

    /// <summary>
    /// Called by the MSI during uninstall. Removes autostart and browser registration; user data (settings,
    /// history, encrypted API key) is kept unless the user chooses secure deletion or --remove-data is passed.
    /// </summary>
    public static int RunUninstallCleanup(bool removeData, bool quiet)
    {
        var paths = KairoPaths.ForCurrentUser();
        var log = new KairoLogger(null);
        try
        {
            var exe = Environment.ProcessPath ?? "";
            new AutostartManager().SetEnabled(false, exe);
            NativeHostRegistrar.Unregister();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            log.Warn("uninstall", $"registry cleanup failed: {ex.GetType().Name}");
        }

        var hasData = paths.AllDataDirectories.Any(Directory.Exists);
        if (!hasData) { return 0; }

        if (!removeData && !quiet)
        {
            var answer = MessageBox.Show(
                "Möchtest du auch deine Kairo-Einstellungen, den Aufgabenverlauf und den verschlüsselt gespeicherten OpenRouter-API-Schlüssel sicher löschen?\n\n" +
                "„Ja“ überschreibt und löscht die Daten. „Nein“ behält sie für eine spätere Neuinstallation.",
                "Kairo deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                // Started by the installer without an owner window: make sure the question is not hidden behind the setup dialog.
                MessageBoxOptions.DefaultDesktopOnly);
            removeData = answer == MessageBoxResult.Yes;
        }

        if (removeData)
        {
            UserDataCleaner.RemoveAll(paths, log);
        }
        return 0;
    }

    private sealed class NullInteraction : IUserInteraction
    {
        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult(ApprovalDecision.Deny);
        public Task<string?> AskUserAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
