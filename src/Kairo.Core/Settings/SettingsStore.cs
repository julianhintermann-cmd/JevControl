using System.Text.Json;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Settings;

/// <summary>Standard data locations of Kairo.</summary>
public sealed class KairoPaths
{
    public KairoPaths(string roamingDirectory, string localDirectory)
    {
        RoamingDirectory = roamingDirectory;
        LocalDirectory = localDirectory;
    }

    /// <summary>%APPDATA%\Kairo – settings.</summary>
    public string RoamingDirectory { get; }

    /// <summary>%LOCALAPPDATA%\Kairo – secrets (DPAPI blobs), history, logs.</summary>
    public string LocalDirectory { get; }

    public string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");
    public string SecretsDirectory => Path.Combine(LocalDirectory, "secrets");
    public string HistoryDirectory => Path.Combine(LocalDirectory, "history");
    public string LogDirectory => Path.Combine(LocalDirectory, "logs");

    public static KairoPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairo"));

    /// <summary>All directories that belong to the user's Kairo data.</summary>
    public IEnumerable<string> AllDataDirectories => [RoamingDirectory, LocalDirectory];
}

/// <summary>Loads and atomically saves <see cref="KairoSettings"/>.</summary>
public sealed class SettingsStore
{
    private readonly KairoPaths _paths;
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private KairoSettings _current;

    public SettingsStore(KairoPaths paths, KairoLogger log)
    {
        _paths = paths;
        _log = log;
        _current = Load();
    }

    public event EventHandler<KairoSettings>? Changed;

    /// <summary>A snapshot copy – modify and pass to <see cref="Save"/>.</summary>
    public KairoSettings Current
    {
        get { lock (_lock) { return _current.Clone(); } }
    }

    private KairoSettings Load()
    {
        var file = _paths.SettingsFile;
        if (!File.Exists(file)) { return new KairoSettings(); }
        try
        {
            var json = File.ReadAllText(file);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.KairoSettings) ?? new KairoSettings();
            return Normalize(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Error("settings", "settings.json is invalid – falling back to defaults", ex);
            try
            {
                File.Copy(file, file + $".broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch (IOException) { }
            return new KairoSettings();
        }
    }

    private static KairoSettings Normalize(KairoSettings s)
    {
        s.General ??= new();
        s.Models ??= new();
        s.Control ??= new();
        s.Security ??= new();
        s.Privacy ??= new();
        s.Appearance ??= new();
        s.Hotkeys ??= new();
        s.Hotkeys.Overlay ??= HotkeyBinding.DefaultOverlay;
        s.Hotkeys.EmergencyStop ??= HotkeyBinding.DefaultEmergencyStop;
        s.Control.MaxActionsPerTask = Math.Clamp(s.Control.MaxActionsPerTask, 5, 500);
        s.Control.MaxPlanningRounds = Math.Clamp(s.Control.MaxPlanningRounds, 1, 50);
        s.Control.MaxRetriesPerStep = Math.Clamp(s.Control.MaxRetriesPerStep, 0, 5);
        s.Control.MaxSnapshotElements = Math.Clamp(s.Control.MaxSnapshotElements, 30, 1000);
        s.Models.JevActThreshold = Math.Clamp(s.Models.JevActThreshold, 0.3, 0.99);
        s.Privacy.MaxFileCharsToModel = Math.Clamp(s.Privacy.MaxFileCharsToModel, 1000, 200_000);
        if (string.IsNullOrWhiteSpace(s.Models.PlannerModel)) { s.Models.PlannerModel = AI.OpenRouter.DefaultModels.Planner; }
        if (string.IsNullOrWhiteSpace(s.Models.DecisionModel)) { s.Models.DecisionModel = AI.OpenRouter.DefaultModels.Decision; }
        if (string.IsNullOrWhiteSpace(s.Models.VisionModel)) { s.Models.VisionModel = AI.OpenRouter.DefaultModels.Vision; }
        s.SchemaVersion = KairoSettings.CurrentSchemaVersion;
        return s;
    }

    public void Save(KairoSettings settings)
    {
        var normalized = Normalize(settings.Clone());
        lock (_lock)
        {
            Directory.CreateDirectory(_paths.RoamingDirectory);
            var json = JsonSerializer.Serialize(normalized, SettingsJsonContext.Default.KairoSettings);
            var temp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _paths.SettingsFile, overwrite: true);
            _current = normalized;
        }
        _log.Info("settings", "settings saved");
        Changed?.Invoke(this, normalized.Clone());
    }

    public void Update(Action<KairoSettings> mutate)
    {
        var copy = Current;
        mutate(copy);
        Save(copy);
    }
}
