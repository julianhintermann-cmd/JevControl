using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;

namespace Kairo.Windows.Automation;

/// <summary>
/// Event based UI change detection: subscribes to UIA structure-changed and window-opened events of the
/// task's target window (only while a task runs – no overhead when idle) and raises debounced notifications
/// that invalidate the snapshot cache.
/// </summary>
public sealed class UiaChangeMonitor : IUiChangeMonitor, IDisposable
{
    private readonly UiaCore _core;
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private IUIAutomationElement? _root;
    private StructureHandler? _structureHandler;
    private nint _window;
    private Timer? _debounce;
    private int _pending;

    public UiaChangeMonitor(UiaCore core, KairoLogger log)
    {
        _core = core;
        _log = log;
    }

    public event EventHandler<UiChangedEventArgs>? Changed;

    public void Watch(WindowInfo window)
    {
        if (window.Handle == 0) { return; }
        lock (_lock)
        {
            if (_window == window.Handle) { return; }
        }
        StopWatching();
        _ = Task.Run(() =>
        {
            try
            {
                var root = _core.Automation.ElementFromHandle(window.Handle);
                var handler = new StructureHandler(this, window.Handle);
                _core.Automation.AddStructureChangedEventHandler(root, TreeScope.TreeScope_Subtree, null, handler);
                lock (_lock)
                {
                    _root = root;
                    _structureHandler = handler;
                    _window = window.Handle;
                }
                _log.Debug("uia", "change monitor attached");
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                _log.Warn("uia", $"change monitor unavailable ({ex.GetType().Name}); falling back to cache expiry");
            }
        });
    }

    public void StopWatching()
    {
        IUIAutomationElement? root;
        StructureHandler? handler;
        lock (_lock)
        {
            root = _root;
            handler = _structureHandler;
            _root = null;
            _structureHandler = null;
            _window = 0;
            _debounce?.Dispose();
            _debounce = null;
        }
        if (root is null || handler is null) { return; }
        handler.Detached = true;
        // Removing handlers must not happen on a UIA callback thread – use a pool thread.
        _ = Task.Run(() =>
        {
            try { _core.Automation.RemoveStructureChangedEventHandler(root, handler); }
            catch (COMException) { }
        });
    }

    private void OnStructureChanged(nint window)
    {
        lock (_lock)
        {
            if (window != _window) { return; }
            Interlocked.Exchange(ref _pending, 1);
            _debounce ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(80, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        nint window;
        lock (_lock) { window = _window; }
        if (window == 0 || Interlocked.Exchange(ref _pending, 0) == 0) { return; }
        Changed?.Invoke(this, new UiChangedEventArgs(window, UiChangeKind.Structure));
    }

    /// <summary>Forwarded browser events (tab switch, navigation, DOM mutations).</summary>
    public void RaiseExternal(nint window, UiChangeKind kind) => Changed?.Invoke(this, new UiChangedEventArgs(window, kind));

    public void Dispose() => StopWatching();

    [ComVisible(true)]
    private sealed class StructureHandler(UiaChangeMonitor owner, nint window) : IUIAutomationStructureChangedEventHandler
    {
        public volatile bool Detached;

        public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, int[] runtimeId)
        {
            if (Detached) { return; }
            owner.OnStructureChanged(window);
        }
    }
}
