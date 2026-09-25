using System.Collections.Concurrent;
using Interop.UIAutomationClient;
using Kairo.Core.Telemetry;

namespace Kairo.Windows.Automation;

/// <summary>
/// Shared UI Automation client (UIA3 / CUIAutomation8). All calls run on thread-pool (MTA) threads.
/// Keeps live element references of recent snapshots so actions do not need to search the tree again.
/// </summary>
public sealed class UiaCore
{
    private readonly KairoLogger _log;
    private readonly Lazy<IUIAutomation> _automation;
    private readonly ConcurrentDictionary<string, (IUIAutomationElement Element, long Stamp)> _registry = new(StringComparer.Ordinal);
    private long _stamp;

    public UiaCore(KairoLogger log)
    {
        _log = log;
        _automation = new Lazy<IUIAutomation>(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IUIAutomation Automation => _automation.Value;

    private IUIAutomation Create()
    {
        IUIAutomation automation;
        try
        {
            automation = new CUIAutomation8();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            automation = new CUIAutomation();
        }

        // Hung applications must not block Kairo: limit connection and transaction time.
        if (automation is IUIAutomation2 a2)
        {
            try
            {
                a2.ConnectionTimeout = 2000;
                a2.TransactionTimeout = 6000;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }
        _log.Info("uia", "UI Automation client created");
        return automation;
    }

    /// <summary>Runs UIA work on an MTA thread-pool thread.</summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken) => Task.Run(work, cancellationToken);

    public static string LocatorFor(int[]? runtimeId) =>
        runtimeId is { Length: > 0 } ? "uia:" + string.Join('.', runtimeId) : "uia:?" + Guid.NewGuid().ToString("N");

    public void Register(string locator, IUIAutomationElement element)
    {
        _registry[locator] = (element, Interlocked.Increment(ref _stamp));
        if (_registry.Count > 6000)
        {
            // Drop the oldest half (elements of old snapshots).
            var cutoff = _stamp - 3000;
            foreach (var kv in _registry)
            {
                if (kv.Value.Stamp < cutoff) { _registry.TryRemove(kv.Key, out _); }
            }
        }
    }

    /// <summary>Returns a live element for a locator (registry first, then a runtime-id search below the window).</summary>
    public IUIAutomationElement? Resolve(string locator, nint windowHandle)
    {
        if (_registry.TryGetValue(locator, out var entry))
        {
            return entry.Element;
        }

        if (!locator.StartsWith("uia:", StringComparison.Ordinal) || windowHandle == 0) { return null; }
        var parts = locator[4..].Split('.');
        if (parts.Length == 0 || !parts.All(p => int.TryParse(p, out _))) { return null; }
        var runtimeId = parts.Select(int.Parse).ToArray();
        try
        {
            var root = Automation.ElementFromHandle(windowHandle);
            var condition = Automation.CreatePropertyCondition(UIA_PropertyIds.UIA_RuntimeIdPropertyId, runtimeId);
            var found = root.FindFirst(TreeScope.TreeScope_Subtree, condition);
            if (found is not null) { Register(locator, found); }
            return found;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _log.Debug("uia", $"resolve failed: {ex.GetType().Name}");
            return null;
        }
    }

    public void Forget(string locator) => _registry.TryRemove(locator, out _);
}
