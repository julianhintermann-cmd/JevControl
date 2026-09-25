using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.Models;
using Kairo.Core.Security;
using Kairo.Windows;

namespace Kairo.App.ViewModels;

public enum OverlayMode
{
    Input,
    Running,
    Approval,
    Question,
    Result,
}

public sealed partial class StepItem : ObservableObject
{
    public required string Text { get; init; }
    public required TaskLogKind Kind { get; init; }
    public bool? Success { get; init; }

    public string Glyph => Kind switch
    {
        TaskLogKind.Plan => "",
        TaskLogKind.Decision => "",
        TaskLogKind.Verification => Success == false ? "" : "",
        TaskLogKind.Approval => "",
        TaskLogKind.Privacy => "",
        TaskLogKind.Warning or TaskLogKind.Error => "",
        _ => Success == false ? "" : "",
    };
}

/// <summary>
/// View model of the command-palette overlay. Also implements <see cref="IUserInteraction"/>:
/// approvals and questions of the running task are shown inside the overlay.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IUserInteraction
{
    private KairoRuntime? _runtime;
    private AgentTask? _task;
    private TaskCompletionSource<ApprovalDecision>? _approval;
    private TaskCompletionSource<string?>? _answer;

    public OverlayViewModel()
    {
    }

    public void Attach(KairoRuntime runtime)
    {
        _runtime = runtime;
        runtime.Tasks.TaskFinished += (_, task) => Dispatch(() => OnTaskFinished(task));
    }

    // ------------------------------------------------------------------ state
    [ObservableProperty] private OverlayMode _mode = OverlayMode.Input;
    [ObservableProperty] private string _instruction = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string _targetAppName = "";
    [ObservableProperty] private ImageSource? _targetAppIcon;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private bool _bannerIsError;
    [ObservableProperty] private string _approvalTitle = "";
    [ObservableProperty] private string _approvalDescription = "";
    [ObservableProperty] private string _approvalReasons = "";
    [ObservableProperty] private string _approvalRisk = "";
    [ObservableProperty] private bool _approvalIsIrreversible;
    [ObservableProperty] private bool _canAllowForTask;
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _answerText = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private bool _resultSuccess;
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private bool _screenshotActive;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private string _hotkeyText = "Strg + Alt + K";

    public ObservableCollection<StepItem> Steps { get; } = [];

    public string StepsSummary => Steps.Count == 0
        ? "Protokoll"
        : $"{Steps.Count} Protokolleinträge · {(ShowDetails ? "ausblenden" : "anzeigen")}";

    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(StepsSummary));

    public WindowInfo? TargetWindow { get; private set; }

    public bool IsBusy => _task is { State: var s } && !s.IsFinal();

    /// <summary>Raised when the view should switch between the full overlay and the compact progress pill.</summary>
    public event EventHandler<OverlayMode>? ModeChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? FocusRequested;

    partial void OnModeChanged(OverlayMode value) => ModeChanged?.Invoke(this, value);

    /// <summary>Called when the hotkey opened the overlay: remembers the window the user was working in.</summary>
    public void PrepareForInput(WindowInfo? target, ImageSource? icon)
    {
        if (IsBusy) { return; }
        TargetWindow = target;
        TargetAppName = target is null ? "" : FriendlyName(target);
        TargetAppIcon = icon;
        Banner = null;
        if (Mode is OverlayMode.Result) { Mode = OverlayMode.Input; }
        if (_runtime is not null && !_runtime.HasApiKey)
        {
            ShowBanner("Es ist kein OpenRouter-API-Schlüssel hinterlegt. Öffne die Einstellungen, um Kairo einzurichten.", true);
        }
        else if (_runtime?.ControlSwitch.IsPaused == true)
        {
            ShowBanner("Die Computersteuerung ist pausiert. Du kannst sie im Tray-Menü fortsetzen.", true);
        }
    }

    private static string FriendlyName(WindowInfo w) =>
        w.AppName != w.ProcessName || string.IsNullOrWhiteSpace(w.Title) ? w.AppName : SnapshotTitle(w.Title);

    private static string SnapshotTitle(string title) => title.Length > 48 ? title[..47] + "…" : title;

    private void ShowBanner(string text, bool error)
    {
        Banner = text;
        BannerIsError = error;
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void Submit()
    {
        if (_runtime is null) { return; }
        var text = Instruction.Trim();
        if (text.Length == 0) { return; }
        if (!_runtime.HasApiKey)
        {
            ShowBanner("Bitte hinterlege zuerst deinen OpenRouter-API-Schlüssel in den Einstellungen.", true);
            return;
        }
        if (IsBusy) { return; }

        Steps.Clear();
        ScreenshotActive = false;
        ShowDetails = false;
        Banner = null;
        StatusText = "Analysiere …";
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            _task = _runtime.Tasks.Start(text, TargetWindow);
        }
        catch (InvalidOperationException ex)
        {
            ShowBanner(ex.Message, true);
            return;
        }
        _task.Changed += OnTaskChanged;
        _task.LogAdded += OnTaskLog;
        Mode = OverlayMode.Running;
        OnPropertyChanged(nameof(IsBusy));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _approval?.TrySetResult(ApprovalDecision.Deny);
            _answer?.TrySetResult(null);
            _runtime?.Tasks.CancelCurrent("Vom Benutzer abgebrochen.");
            StatusText = "Breche ab …";
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Allow() => _approval?.TrySetResult(ApprovalDecision.AllowOnce);

    [RelayCommand]
    private void AllowForTask() => _approval?.TrySetResult(ApprovalDecision.AllowForTask);

    [RelayCommand]
    private void Deny() => _approval?.TrySetResult(ApprovalDecision.Deny);

    [RelayCommand]
    private void SendAnswer()
    {
        var text = AnswerText.Trim();
        if (text.Length == 0) { return; }
        _answer?.TrySetResult(text);
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy) { return; }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NewTask()
    {
        if (IsBusy) { return; }
        Instruction = "";
        Mode = OverlayMode.Input;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    // ------------------------------------------------------------------ task events
    private void OnTaskChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        if (sender is not AgentTask task || task != _task) { return; }
        StatusText = task.StatusText;
        if (task.Progress is { } p)
        {
            IsProgressIndeterminate = task.State is AgentTaskState.Planning or AgentTaskState.Verifying;
            Progress = p * 100;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
        ScreenshotActive = task.ScreenshotSent && task.State == AgentTaskState.Executing;
    });

    private void OnTaskLog(object? sender, TaskLogEntry entry) => Dispatch(() =>
    {
        Steps.Add(new StepItem { Text = entry.Text, Kind = entry.Kind, Success = entry.Success });
        while (Steps.Count > 40) { Steps.RemoveAt(0); }
        OnPropertyChanged(nameof(StepsSummary));
        if (entry.Kind == TaskLogKind.Privacy) { ScreenshotActive = true; }
    });

    private void OnTaskFinished(AgentTask task)
    {
        if (task != _task) { return; }
        task.Changed -= OnTaskChanged;
        task.LogAdded -= OnTaskLog;
        _approval?.TrySetResult(ApprovalDecision.Deny);
        _answer?.TrySetResult(null);
        ResultSuccess = task.State == AgentTaskState.Completed;
        ResultMessage = task.State switch
        {
            AgentTaskState.Completed => task.ResultMessage ?? "Erledigt.",
            AgentTaskState.Cancelled => task.ErrorMessage ?? "Abgebrochen.",
            _ => task.ErrorMessage ?? "Die Aufgabe ist fehlgeschlagen.",
        };
        var cost = task.Metrics.TotalCost;
        ResultDetails = string.Create(CultureInfo.GetCultureInfo("de-CH"),
            $"{task.CompletedActions} Aktionen · {task.PlanningRounds} Planungsrunde(n) · {task.Duration.TotalSeconds:0.0} s · {task.Metrics.ModelCalls} Modellaufrufe ({task.Metrics.DecisionCalls} Jev) · Kosten ${cost:0.0000}");
        ScreenshotActive = false;
        Mode = OverlayMode.Result;
        OnPropertyChanged(nameof(IsBusy));
        if (ResultSuccess) { Instruction = ""; }
    }

    // ------------------------------------------------------------------ IUserInteraction
    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            _approval = tcs;
            ApprovalTitle = request.Title;
            ApprovalDescription = request.Description;
            ApprovalReasons = string.Join(Environment.NewLine, request.Reasons.Select(r => "• " + r));
            ApprovalRisk = RiskClassifier.Describe(request.Risk);
            ApprovalIsIrreversible = request.Risk == RiskLevel.Irreversible;
            CanAllowForTask = request.Risk == RiskLevel.Sensitive && request.Action.Kind != ActionKind.RequestVision;
            Mode = OverlayMode.Approval;
            FocusRequested?.Invoke(this, EventArgs.Empty);
        });
        using var registration = cancellationToken.Register(() => tcs.TrySetResult(ApprovalDecision.Deny));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Dispatch(() =>
            {
                if (_approval == tcs) { _approval = null; }
                if (Mode == OverlayMode.Approval && IsBusy) { Mode = OverlayMode.Running; }
            });
        }
    }

    public async Task<string?> AskUserAsync(string question, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            _answer = tcs;
            Question = question;
            AnswerText = "";
            Mode = OverlayMode.Question;
            FocusRequested?.Invoke(this, EventArgs.Empty);
        });
        using var registration = cancellationToken.Register(() => tcs.TrySetResult(null));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Dispatch(() =>
            {
                if (_answer == tcs) { _answer = null; }
                if (Mode == OverlayMode.Question && IsBusy) { Mode = OverlayMode.Running; }
            });
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); }
        else { dispatcher.BeginInvoke(action); }
    }
}
