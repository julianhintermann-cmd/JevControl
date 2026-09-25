using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kairo.App.Services;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.History;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Windows;
using Kairo.Windows.Hotkeys;
using Kairo.Windows.Security;

namespace Kairo.App.ViewModels;

/// <summary>Operations of the running application that settings need (implemented by the app controller).</summary>
public interface IAppHost
{
    KairoRuntime Runtime { get; }
    HotkeyRegistrationResult ProbeHotkey(HotkeyBinding binding);
    (HotkeyRegistrationResult Overlay, HotkeyRegistrationResult Stop) ApplyHotkeys();
    void SetAutostart(bool enabled);
    void ApplyAppearance();
    bool ControlPaused { get; set; }
    void Shutdown();
}

public sealed record NavEntry(string Key, string Title, string Glyph);

public sealed class HistoryItem
{
    public required TaskHistoryRecord Record { get; init; }
    public string Title => string.IsNullOrWhiteSpace(Record.Instruction) ? "(Anweisung nicht gespeichert)" : Record.Instruction!;
    public string When => Record.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    public string StateText => Record.State switch
    {
        "Completed" => "Erledigt",
        "Cancelled" => "Abgebrochen",
        "Failed" => "Fehlgeschlagen",
        _ => Record.State,
    };
    public bool Success => Record.State == "Completed";
    public string Summary => string.Create(CultureInfo.GetCultureInfo("de-CH"),
        $"{Record.Actions} Aktionen · {Record.DurationMs / 1000:0.0} s · {Record.ModelCalls} Modellaufrufe ({Record.DecisionCalls} Jev) · ${Record.Cost:0.0000}{(Record.ScreenshotSent ? " · Screenshot gesendet" : "")}");
    public string Latencies => string.Join(Environment.NewLine, Record.LatencyMs.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}: {kv.Value:0} ms"));
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppHost _host;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;

    public SettingsViewModel(IAppHost host)
    {
        _host = host;
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        Model = host.Runtime.Settings.Current;
        Navigation =
        [
            new("general", "Allgemein", ""),
            new("models", "API und Modelle", ""),
            new("control", "Computersteuerung", ""),
            new("security", "Sicherheit und Berechtigungen", ""),
            new("privacy", "Datenschutz", ""),
            new("appearance", "Erscheinungsbild", ""),
            new("hotkeys", "Tastenkombinationen", ""),
            new("history", "Aufgabenverlauf", ""),
            new("about", "Über Kairo", ""),
        ];
        _selectedNav = Navigation[0];
        Reload();
    }

    public KairoRuntime Runtime => _host.Runtime;

    public KairoSettings Model { get; private set; }

    public IReadOnlyList<NavEntry> Navigation { get; }

    [ObservableProperty] private NavEntry _selectedNav;
    [ObservableProperty] private string _newApiKey = "";
    [ObservableProperty] private bool _revealKey;
    [ObservableProperty] private bool _hasKey;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _costSummary = "";
    [ObservableProperty] private string _browserStatus = "";
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private HotkeyBinding? _overlayHotkey;
    [ObservableProperty] private HotkeyBinding? _stopHotkey;
    [ObservableProperty] private bool _controlPaused;
    [ObservableProperty] private HistoryItem? _selectedHistory;
    [ObservableProperty] private string _modelListStatus = "";
    [ObservableProperty] private string _newBlockedApp = "";
    [ObservableProperty] private string _reasoningEffort = "Standard";

    public ObservableCollection<CheckResult> Results { get; } = [];
    public ObservableCollection<string> PlannerModels { get; } = [.. DefaultModels.PlannerPreference];
    public ObservableCollection<string> VisionModels { get; } = [.. DefaultModels.VisionPreference];
    public IReadOnlyList<string> DecisionModels { get; } = DefaultModels.DecisionAlternatives;
    public IReadOnlyList<string> ReasoningOptions { get; } = ["Standard", "low", "medium", "high"];
    public ObservableCollection<string> BlockedApps { get; } = [];
    public ObservableCollection<string> ReadRoots { get; } = [];
    public ObservableCollection<string> WriteRoots { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public bool HasHistory => History.Count > 0;
    public ObservableCollection<string> RecentLog { get; } = [];

    public string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string DataFolder => Runtime.Paths.LocalDirectory;
    public string DefaultFolders => string.Join(Environment.NewLine, Kairo.Core.Security.FileAccessPolicy.StandardUserFolders());

    public void Reload()
    {
        _loading = true;
        Model = Runtime.Settings.Current;
        OnPropertyChanged(nameof(Model));
        HasKey = Runtime.HasApiKey;
        KeyStatus = HasKey ? "Ein API-Schlüssel ist verschlüsselt gespeichert (Windows DPAPI, nur für dein Benutzerkonto lesbar)." : "Es ist kein API-Schlüssel gespeichert.";
        OverlayHotkey = Model.Hotkeys.Overlay;
        StopHotkey = Model.Hotkeys.EmergencyStop;
        ControlPaused = _host.ControlPaused;
        ReasoningEffort = string.IsNullOrWhiteSpace(Model.Models.PlannerReasoningEffort) ? "Standard" : Model.Models.PlannerReasoningEffort!;
        Replace(BlockedApps, Model.Security.BlockedApplications);
        Replace(ReadRoots, Model.Security.AllowedReadRoots);
        Replace(WriteRoots, Model.Security.AllowedWriteRoots);
        if (!PlannerModels.Contains(Model.Models.PlannerModel)) { PlannerModels.Insert(0, Model.Models.PlannerModel); }
        if (!VisionModels.Contains(Model.Models.VisionModel)) { VisionModels.Insert(0, Model.Models.VisionModel); }
        RefreshStatus();
        _loading = false;
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var v in values) { target.Add(v); }
    }

    public void RefreshStatus()
    {
        var connections = Runtime.Bridge.Connections;
        BrowserStatus = connections.Count == 0
            ? "Keine Browser-Erweiterung verbunden. Kairo nutzt im Browser Windows UI Automation."
            : "Verbunden: " + string.Join(", ", connections.Select(c => $"{c.DisplayName} (Erweiterung {c.Version})"));
        var history = Runtime.History;
        var today = history.TotalCost(DateTimeOffset.Now.Date);
        var month = history.TotalCost(DateTimeOffset.Now.AddDays(-30));
        CostSummary = string.Create(CultureInfo.GetCultureInfo("de-CH"),
            $"Diese Sitzung: ${Runtime.Usage.SessionCost:0.0000} · Heute: ${today:0.0000} · Letzte 30 Tage: ${month:0.0000}");
        History.Clear();
        foreach (var r in history.GetAll()) { History.Add(new HistoryItem { Record = r }); }
        RecentLog.Clear();
        foreach (var e in Runtime.Log.Recent.TakeLast(60).Reverse())
        {
            RecentLog.Add($"{e.Time:HH:mm:ss} {e.Level,-7} {e.Category}: {e.Message}");
        }
    }

    /// <summary>Called by the view whenever an input changed (debounced auto-save, like Windows Settings).</summary>
    public void ScheduleSave()
    {
        if (_loading) { return; }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        Model.Security.BlockedApplications = [.. BlockedApps];
        Model.Security.AllowedReadRoots = [.. ReadRoots];
        Model.Security.AllowedWriteRoots = [.. WriteRoots];
        Model.Models.PlannerReasoningEffort = ReasoningEffort == "Standard" ? null : ReasoningEffort;
        var previous = Runtime.Settings.Current;
        Runtime.Settings.Save(Model);
        if (previous.General.StartWithWindows != Model.General.StartWithWindows) { _host.SetAutostart(Model.General.StartWithWindows); }
        if (previous.Appearance.Theme != Model.Appearance.Theme || previous.Appearance.UseMica != Model.Appearance.UseMica) { _host.ApplyAppearance(); }
    }

    partial void OnReasoningEffortChanged(string value) => ScheduleSave();

    partial void OnControlPausedChanged(bool value)
    {
        if (!_loading) { _host.ControlPaused = value; }
    }

    partial void OnOverlayHotkeyChanged(HotkeyBinding? value)
    {
        if (_loading || value is null) { return; }
        Model.Hotkeys.Overlay = value;
        Runtime.Settings.Save(Model);
        ApplyHotkeys();
    }

    partial void OnStopHotkeyChanged(HotkeyBinding? value)
    {
        if (_loading || value is null) { return; }
        Model.Hotkeys.EmergencyStop = value;
        Runtime.Settings.Save(Model);
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        var (overlay, stop) = _host.ApplyHotkeys();
        HotkeyStatus = $"{Describe(Model.Hotkeys.Overlay, overlay)}\n{Describe(Model.Hotkeys.EmergencyStop, stop)}";
    }

    private static string Describe(HotkeyBinding binding, HotkeyRegistrationResult result) => result switch
    {
        HotkeyRegistrationResult.Registered => $"✓ {binding} ist aktiv.",
        HotkeyRegistrationResult.AlreadyInUse => $"✗ {binding} wird bereits von einer anderen Anwendung verwendet.",
        HotkeyRegistrationResult.Invalid => $"✗ {binding} ist keine gültige Kombination.",
        _ => $"✗ {binding} konnte nicht registriert werden.",
    };

    [RelayCommand]
    private void ResetHotkeys()
    {
        _loading = true;
        OverlayHotkey = HotkeyBinding.DefaultOverlay;
        StopHotkey = HotkeyBinding.DefaultEmergencyStop;
        _loading = false;
        Model.Hotkeys.Overlay = HotkeyBinding.DefaultOverlay;
        Model.Hotkeys.EmergencyStop = HotkeyBinding.DefaultEmergencyStop;
        Runtime.Settings.Save(Model);
        ApplyHotkeys();
    }

    // ------------------------------------------------------------------ API key
    [RelayCommand]
    private void SaveKey()
    {
        var key = NewApiKey.Trim();
        if (key.Length < 10)
        {
            TestMessage = "Bitte gib einen gültigen OpenRouter-API-Schlüssel ein.";
            return;
        }
        Runtime.Secrets.SetSecret(KairoRuntime.ApiKeySecretName, key);
        NewApiKey = "";
        HasKey = true;
        KeyStatus = "Neuer API-Schlüssel verschlüsselt gespeichert.";
        TestMessage = "";
    }

    [RelayCommand]
    private void RemoveKey()
    {
        var answer = MessageBox.Show("Soll der gespeicherte OpenRouter-API-Schlüssel sicher gelöscht werden? Kairo kann danach keine Aufgaben mehr ausführen, bis du einen neuen Schlüssel hinterlegst.",
            "API-Schlüssel entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        Runtime.Secrets.DeleteSecret(KairoRuntime.ApiKeySecretName);
        HasKey = false;
        KeyStatus = "Der API-Schlüssel wurde sicher gelöscht.";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        Results.Clear();
        TestMessage = "Verbindung wird getestet …";
        try
        {
            var key = NewApiKey.Trim().Length > 0 ? NewApiKey.Trim() : Runtime.Secrets.GetSecret(KairoRuntime.ApiKeySecretName) ?? "";
            if (key.Length == 0)
            {
                TestMessage = "Es ist kein API-Schlüssel vorhanden.";
                return;
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var report = await KeyTester.TestAsync(key, Model.Models.PlannerModel, Model.Models.DecisionModel, Model.Models.VisionEnabled ? Model.Models.VisionModel : null, cts.Token);
            Results.Add(new CheckResult("API-Schlüssel", report.KeyMessage, report.KeyValid));
            foreach (var m in report.Models)
            {
                Results.Add(new CheckResult($"{m.Role}: {m.ModelId}", m.Available || m.Suggestion is null ? m.Message : $"{m.Message} Vorschlag: {m.Suggestion}", m.Available, m.Suggestion));
            }
            TestMessage = report.AllAvailable ? "Alle Modelle sind verfügbar." : report.KeyValid ? "Einige Modelle sind nicht verfügbar – siehe Vorschläge." : report.KeyMessage;
        }
        catch (OperationCanceledException)
        {
            TestMessage = "Zeitüberschreitung beim Verbindungstest.";
        }
        catch (Exception ex)
        {
            TestMessage = $"Test fehlgeschlagen: {Redactor.Redact(ex.Message)}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        ModelListStatus = "Lade Modellliste …";
        try
        {
            var models = await Runtime.OpenRouter.ListModelsAsync(CancellationToken.None, forceRefresh: true);
            PlannerModels.Clear();
            VisionModels.Clear();
            foreach (var m in models.Where(m => !m.Id.Contains("jev", StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            {
                PlannerModels.Add(m.Id);
                if (m.SupportsImages) { VisionModels.Add(m.Id); }
            }
            ModelListStatus = $"{PlannerModels.Count} Modelle verfügbar, davon {VisionModels.Count} mit Bildverständnis.";
        }
        catch (OpenRouterException ex)
        {
            ModelListStatus = ex.UserMessage;
        }
    }

    // ------------------------------------------------------------------ security lists
    [RelayCommand]
    private void AddBlockedApp()
    {
        var name = NewBlockedApp.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0 || BlockedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) { return; }
        BlockedApps.Add(name);
        NewBlockedApp = "";
        ScheduleSave();
    }

    [RelayCommand]
    private void RemoveBlockedApp(string? name)
    {
        if (name is not null && BlockedApps.Remove(name)) { ScheduleSave(); }
    }

    [RelayCommand]
    private void AddReadRoot() => AddFolder(ReadRoots);

    [RelayCommand]
    private void AddWriteRoot() => AddFolder(WriteRoots);

    [RelayCommand]
    private void RemoveReadRoot(string? path)
    {
        if (path is not null && ReadRoots.Remove(path)) { ScheduleSave(); }
    }

    [RelayCommand]
    private void RemoveWriteRoot(string? path)
    {
        if (path is not null && WriteRoots.Remove(path)) { ScheduleSave(); }
    }

    private void AddFolder(ObservableCollection<string> target)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner freigeben", Multiselect = false };
        if (dialog.ShowDialog() == true && !target.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(dialog.FolderName);
            ScheduleSave();
        }
    }

    // ------------------------------------------------------------------ privacy / data
    [RelayCommand]
    private void ClearHistory()
    {
        if (MessageBox.Show("Den gesamten Aufgabenverlauf sicher löschen?", "Verlauf löschen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
        Runtime.History.Clear();
        RefreshStatus();
    }

    [RelayCommand]
    private void RemoveAllData()
    {
        var answer = MessageBox.Show(
            "Alle Kairo-Daten sicher löschen? Einstellungen, Aufgabenverlauf, Protokolle und der verschlüsselte API-Schlüssel werden überschrieben und entfernt. Kairo wird anschließend beendet.",
            "Alle Daten entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { return; }
        Runtime.Tasks.CancelCurrent("Daten werden gelöscht.");
        Runtime.Secrets.DeleteAll();
        Runtime.History.Clear();
        Runtime.Log.Dispose();
        UserDataCleaner.RemoveAll(Runtime.Paths, KairoLogger.Null);
        _host.SetAutostart(false);
        _host.Shutdown();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ browser extension
    [RelayCommand]
    private void SetupExtension()
    {
        var extensionFolder = Path.Combine(AppContext.BaseDirectory, "extension");
        var host = Path.Combine(AppContext.BaseDirectory, "Kairo.BrowserHost.exe");
        if (File.Exists(host) && !NativeHostRegistrar.IsRegistered())
        {
            NativeHostRegistrar.Register(host, AppContext.BaseDirectory);
        }
        if (Directory.Exists(extensionFolder))
        {
            System.Windows.Clipboard.SetText(extensionFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{extensionFolder}\"") { UseShellExecute = true });
        }
        MessageBox.Show(
            "So richtest du die Kairo-Erweiterung ein:\n\n" +
            "1. Öffne in Chrome chrome://extensions bzw. in Edge edge://extensions.\n" +
            "2. Aktiviere den Entwicklermodus.\n" +
            "3. Klicke auf „Entpackte Erweiterung laden“ und wähle den Ordner, der eben geöffnet wurde (der Pfad ist in der Zwischenablage).\n\n" +
            "Die Erweiterung verbindet sich danach automatisch mit Kairo.",
            "Browser-Erweiterung einrichten", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshStatus();
    }

    [RelayCommand]
    private void RefreshBrowser() => RefreshStatus();

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    }
}
