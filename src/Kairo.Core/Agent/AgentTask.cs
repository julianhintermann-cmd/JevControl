using System.Collections.ObjectModel;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Agent;

/// <summary>Lifecycle states of a task.</summary>
public enum AgentTaskState
{
    Pending,
    Planning,
    Executing,
    Verifying,
    WaitingForApproval,
    Completed,
    Failed,
    Cancelled,
}

public static class AgentTaskStateExtensions
{
    public static bool IsFinal(this AgentTaskState state) =>
        state is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Cancelled;

    public static string ToGerman(this AgentTaskState state) => state switch
    {
        AgentTaskState.Pending => "Wartet",
        AgentTaskState.Planning => "Plant",
        AgentTaskState.Executing => "Führt aus",
        AgentTaskState.Verifying => "Überprüft",
        AgentTaskState.WaitingForApproval => "Wartet auf Freigabe",
        AgentTaskState.Completed => "Erledigt",
        AgentTaskState.Failed => "Fehlgeschlagen",
        AgentTaskState.Cancelled => "Abgebrochen",
        _ => state.ToString(),
    };
}

public enum TaskLogKind
{
    Info,
    Plan,
    Decision,
    Action,
    Verification,
    Approval,
    Privacy,
    Warning,
    Error,
}

/// <summary>User visible log entry (no secrets, values are never logged).</summary>
public sealed record TaskLogEntry(DateTimeOffset Time, TaskLogKind Kind, string Text, bool? Success = null);

/// <summary>A running or finished task. Thread-safe notifications for the UI.</summary>
public sealed class AgentTask
{
    private readonly object _lock = new();
    private readonly List<TaskLogEntry> _log = [];
    private AgentTaskState _state = AgentTaskState.Pending;
    private string _status = "Wird vorbereitet …";

    public AgentTask(string instruction, WindowInfo? targetWindow)
    {
        Instruction = instruction;
        TargetWindow = targetWindow;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Instruction { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; private set; }
    public WindowInfo? TargetWindow { get; internal set; }
    public TaskMetrics Metrics { get; } = new();
    internal ExecutionGate? Gate { get; set; }

    public AgentTaskState State { get { lock (_lock) { return _state; } } }
    public string StatusText { get { lock (_lock) { return _status; } } }
    public string? ResultMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int CompletedActions { get; internal set; }
    public int PlannedActions { get; internal set; }
    public int PlanningRounds { get; internal set; }
    public bool ScreenshotSent { get; internal set; }

    public ApprovalRequest? PendingApproval { get; internal set; }
    public string? PendingQuestion { get; internal set; }

    public IReadOnlyList<TaskLogEntry> Log { get { lock (_lock) { return _log.ToList(); } } }

    /// <summary>0..1 or null when unknown.</summary>
    public double? Progress => PlannedActions <= 0 ? null : Math.Clamp((double)CompletedActions / PlannedActions, 0, 1);

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.Now) - CreatedAt;

    public event EventHandler? Changed;
    public event EventHandler<TaskLogEntry>? LogAdded;

    internal void SetState(AgentTaskState state, string? status = null)
    {
        lock (_lock)
        {
            if (_state.IsFinal()) { return; }
            _state = state;
            if (status is not null) { _status = status; }
            if (state.IsFinal()) { FinishedAt = DateTimeOffset.Now; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetStatus(string status)
    {
        lock (_lock) { _status = status; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void AddLog(TaskLogKind kind, string text, bool? success = null)
    {
        var entry = new TaskLogEntry(DateTimeOffset.Now, kind, text, success);
        lock (_lock) { _log.Add(entry); }
        LogAdded?.Invoke(this, entry);
    }

    internal void Complete(string message)
    {
        ResultMessage = message;
        SetState(AgentTaskState.Completed, message);
    }

    internal void Fail(string message)
    {
        ErrorMessage = message;
        SetState(AgentTaskState.Failed, message);
    }

    internal void MarkCancelled(string message)
    {
        ErrorMessage = message;
        SetState(AgentTaskState.Cancelled, message);
    }

    internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
