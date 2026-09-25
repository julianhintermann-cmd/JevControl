namespace Kairo.Core.Abstractions;

/// <summary>
/// Global kill switch for computer control (tray menu "Computersteuerung pausieren").
/// While paused no task can execute any action.
/// </summary>
public sealed class ComputerControlSwitch
{
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public event EventHandler<bool>? PausedChanged;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) { return; }
        _paused = paused;
        PausedChanged?.Invoke(this, paused);
    }
}

/// <summary>
/// Every single real-world action passes through this gate. Once closed (cancel, pause, failure),
/// it can never be reopened, so no further action can run after a task was stopped.
/// </summary>
public sealed class ExecutionGate : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly ComputerControlSwitch? _switch;
    private volatile string? _closedReason;

    public ExecutionGate(CancellationToken linkedToken = default, ComputerControlSwitch? controlSwitch = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        _switch = controlSwitch;
        if (_switch is not null)
        {
            _switch.PausedChanged += OnPausedChanged;
        }
    }

    public CancellationToken Token => _cts.Token;

    public bool IsOpen => _closedReason is null && !_cts.IsCancellationRequested && _switch?.IsPaused != true;

    public string? ClosedReason => _closedReason ?? (_switch?.IsPaused == true ? "Computersteuerung pausiert" : _cts.IsCancellationRequested ? "Abgebrochen" : null);

    /// <summary>Throws <see cref="OperationCanceledException"/> when no further actions may run.</summary>
    public void ThrowIfClosed()
    {
        if (!IsOpen)
        {
            throw new OperationCanceledException(ClosedReason ?? "Abgebrochen", _cts.Token);
        }
    }

    public void Close(string reason)
    {
        _closedReason ??= reason;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void OnPausedChanged(object? sender, bool paused)
    {
        if (paused) { Close("Computersteuerung pausiert"); }
    }

    public void Dispose()
    {
        if (_switch is not null) { _switch.PausedChanged -= OnPausedChanged; }
        _cts.Dispose();
    }
}
