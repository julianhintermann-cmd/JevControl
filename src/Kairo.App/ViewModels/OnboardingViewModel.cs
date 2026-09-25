using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kairo.App.Services;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.Settings;
using Kairo.Windows;
using Kairo.Windows.Hotkeys;

namespace Kairo.App.ViewModels;

public sealed record CheckResult(string Title, string Detail, bool Ok, string? Model = null)
{
    public string Glyph => Ok ? "" : "";
}

/// <summary>First-run setup: API key (DPAPI), connection and model check, hotkey, autostart.</summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly KairoRuntime _runtime;
    private readonly Func<HotkeyBinding, HotkeyRegistrationResult> _probeHotkey;
    private readonly Action<bool> _setAutostart;
    private CancellationTokenSource? _testCts;

    public OnboardingViewModel(KairoRuntime runtime, Func<HotkeyBinding, HotkeyRegistrationResult> probeHotkey, Action<bool> setAutostart)
    {
        _runtime = runtime;
        _probeHotkey = probeHotkey;
        _setAutostart = setAutostart;
        var s = runtime.Settings.Current;
        _plannerModel = s.Models.PlannerModel;
        _decisionModel = s.Models.DecisionModel;
        _visionModel = s.Models.VisionModel;
        _autostart = s.General.StartWithWindows;
        _hasStoredKey = runtime.HasApiKey;
        RefreshHotkeyStatus();
    }

    public IReadOnlyList<string> PlannerSuggestions { get; } = DefaultModels.PlannerPreference;
    public IReadOnlyList<string> DecisionSuggestions { get; } = DefaultModels.DecisionAlternatives;
    public IReadOnlyList<string> VisionSuggestions { get; } = DefaultModels.VisionPreference;

    public ObservableCollection<CheckResult> Results { get; } = [];

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _revealKey;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool? _keyValid;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _plannerModel;
    [ObservableProperty] private string _decisionModel;
    [ObservableProperty] private string _visionModel;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private bool _hotkeyOk;
    [ObservableProperty] private bool _hasStoredKey;

    public event EventHandler? Completed;

    public bool IsLastStep => Step == 3;
    public string NextText => Step switch { 0 => "Los geht's", 3 => "Fertig", _ => "Weiter" };

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextText));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    partial void OnApiKeyChanged(string value)
    {
        KeyValid = null;
        TestMessage = "";
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnKeyValidChanged(bool? value) => NextCommand.NotifyCanExecuteChanged();

    private bool CanGoNext() => Step switch
    {
        1 => HasStoredKey && ApiKey.Length == 0 || KeyValid == true || (ApiKey.Trim().StartsWith("sk-or-", StringComparison.Ordinal) && KeyValid is null && !IsTesting && TestMessage.Length > 0),
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        switch (Step)
        {
            case 1:
                if (ApiKey.Trim().Length > 0)
                {
                    _runtime.Secrets.SetSecret(KairoRuntime.ApiKeySecretName, ApiKey.Trim());
                    HasStoredKey = true;
                    ApiKey = "";
                }
                _runtime.Settings.Update(s =>
                {
                    s.Models.PlannerModel = PlannerModel.Trim();
                    s.Models.DecisionModel = DecisionModel.Trim();
                    s.Models.VisionModel = VisionModel.Trim();
                });
                Step++;
                break;
            case 3:
                _runtime.Settings.Update(s =>
                {
                    s.General.StartWithWindows = Autostart;
                    s.OnboardingCompleted = true;
                });
                _setAutostart(Autostart);
                Completed?.Invoke(this, EventArgs.Empty);
                break;
            default:
                Step++;
                break;
        }
    }

    private bool CanGoBack() => Step > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step--;

    [RelayCommand]
    private async Task TestAsync()
    {
        var key = ApiKey.Trim();
        if (key.Length == 0)
        {
            TestMessage = "Bitte gib zuerst deinen API-Schlüssel ein.";
            return;
        }
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        IsTesting = true;
        KeyValid = null;
        Results.Clear();
        TestMessage = "Verbindung wird getestet …";
        try
        {
            var report = await KeyTester.TestAsync(key, PlannerModel.Trim(), DecisionModel.Trim(), VisionModel.Trim(), _testCts.Token);
            Results.Add(new CheckResult("API-Schlüssel", report.KeyMessage, report.KeyValid));
            foreach (var m in report.Models)
            {
                var detail = m.Available ? m.Message : m.Suggestion is null ? m.Message : $"{m.Message} Vorschlag: {m.Suggestion}";
                Results.Add(new CheckResult($"{m.Role}: {m.ModelId}", detail, m.Available, m.Suggestion));
            }

            // Apply suggestions for unavailable models automatically.
            foreach (var m in report.Models.Where(m => !m.Available && m.Suggestion is not null))
            {
                if (m.Role.StartsWith("Planungs", StringComparison.Ordinal)) { PlannerModel = m.Suggestion!; }
                else if (m.Role.StartsWith("Vision", StringComparison.Ordinal)) { VisionModel = m.Suggestion!; }
                else if (m.Role.StartsWith("Entscheidungs", StringComparison.Ordinal)) { DecisionModel = m.Suggestion!; }
            }

            KeyValid = report.KeyValid;
            TestMessage = !report.KeyValid ? report.KeyMessage :
                report.AllAvailable ? "Alles bereit. Kairo kann OpenRouter und Jev verwenden." :
                "Der Schlüssel ist gültig. Nicht verfügbare Modelle wurden durch Vorschläge ersetzt – teste erneut, um sie zu prüfen.";
        }
        catch (OperationCanceledException)
        {
            TestMessage = "Der Test hat zu lange gedauert. Prüfe deine Internetverbindung.";
        }
        catch (Exception ex)
        {
            TestMessage = $"Test fehlgeschlagen: {Kairo.Core.Telemetry.Redactor.Redact(ex.Message)}";
        }
        finally
        {
            IsTesting = false;
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private static void OpenKeyPage() => Process.Start(new ProcessStartInfo("https://openrouter.ai/keys") { UseShellExecute = true });

    [RelayCommand]
    private void RefreshHotkeyStatus()
    {
        var binding = _runtime.Settings.Current.Hotkeys.Overlay;
        var result = _probeHotkey(binding);
        HotkeyOk = result == HotkeyRegistrationResult.Registered;
        HotkeyStatus = result switch
        {
            HotkeyRegistrationResult.Registered => $"{binding} ist aktiv.",
            HotkeyRegistrationResult.AlreadyInUse => $"{binding} wird bereits von einer anderen Anwendung verwendet. Wähle in den Einstellungen eine andere Kombination.",
            _ => $"{binding} konnte nicht registriert werden.",
        };
    }
}
