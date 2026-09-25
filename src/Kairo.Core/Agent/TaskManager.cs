using Kairo.Core.History;
using Kairo.Core.Models;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Agent;

/// <summary>
/// Owns the lifecycle of tasks: only one task runs at a time, it can be cancelled at any moment,
/// finished tasks are kept in a short in-memory list (tray "Letzte Aufgaben") and in the encrypted history.
/// </summary>
public sealed class TaskManager
{
    private readonly AgentRunner _runner;
    private readonly TaskHistoryStore? _history;
    private readonly Func<KairoSettings> _settings;
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private readonly List<AgentTask> _recent = [];
    private CancellationTokenSource? _cts;

    public TaskManager(AgentRunner runner, TaskHistoryStore? history, Func<KairoSettings> settings, KairoLogger log)
    {
        _runner = runner;
        _history = history;
        _settings = settings;
        _log = log;
    }

    public AgentTask? Current { get; private set; }

    public Task? CurrentRun { get; private set; }

    public bool IsBusy => Current is { State: var s } && !s.IsFinal();

    public IReadOnlyList<AgentTask> Recent { get { lock (_lock) { return _recent.ToList(); } } }

    public event EventHandler<AgentTask>? TaskStarted;
    public event EventHandler<AgentTask>? TaskFinished;

    public AgentTask Start(string instruction, WindowInfo? targetWindow)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new ArgumentException("Die Anweisung ist leer.", nameof(instruction));
        }

        CancellationTokenSource cts;
        AgentTask task;
        lock (_lock)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("Es läuft bereits eine Aufgabe.");
            }
            _cts?.Dispose();
            cts = _cts = new CancellationTokenSource();
            task = new AgentTask(instruction.Trim(), targetWindow);
            Current = task;
        }

        _log.Info("tasks", $"task {task.Id:N} started");
        TaskStarted?.Invoke(this, task);
        CurrentRun = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(task, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                OnFinished(task);
            }
        });
        return task;
    }

    /// <summary>Stops the current task immediately. No further action is executed afterwards.</summary>
    public void CancelCurrent(string reason = "Vom Benutzer abgebrochen.")
    {
        AgentTask? task;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            task = Current;
            cts = _cts;
        }
        if (task is null || task.State.IsFinal()) { return; }
        _log.Info("tasks", $"task {task.Id:N} cancel requested");
        task.Gate?.Close(reason);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void OnFinished(AgentTask task)
    {
        lock (_lock)
        {
            _recent.Insert(0, task);
            if (_recent.Count > 10) { _recent.RemoveAt(_recent.Count - 1); }
        }

        var settings = _settings();
        if (_history is not null && settings.Privacy.SaveTaskHistory)
        {
            try
            {
                _history.Add(TaskHistoryRecord.FromTask(task, settings.Privacy.StoreInstructionsInHistory), settings.Privacy.HistoryRetentionDays);
            }
            catch (Exception ex)
            {
                _log.Warn("tasks", $"history write failed: {ex.GetType().Name}");
            }
        }
        TaskFinished?.Invoke(this, task);
    }
}
