using System.Collections.Concurrent;
using System.Diagnostics;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Perception;

/// <summary>
/// Multi-stage perception: Browser DOM (priority) → UI Automation → (explicit) Vision.
/// Snapshots are cached per window and invalidated event-based (UIA structure events, browser events)
/// or after actions that change the structure – unchanged UIs are never read twice.
/// </summary>
public sealed class PerceptionService
{
    private readonly IReadOnlyList<IPerceptionProvider> _providers;
    private readonly IUiChangeMonitor? _monitor;
    private readonly UsageTracker _usage;
    private readonly KairoLogger _log;
    private readonly ConcurrentDictionary<nint, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<nint, Task<UiSnapshot?>> _prefetch = new();
    private readonly ConcurrentDictionary<nint, long> _generation = new();

    private sealed record CacheEntry(UiSnapshot Snapshot, long Version, DateTimeOffset CapturedAt);

    private long _version;

    public PerceptionService(IEnumerable<IPerceptionProvider> providers, IUiChangeMonitor? monitor, UsageTracker usage, KairoLogger log)
    {
        _providers = providers.Where(p => p.Source != PerceptionSource.Vision).OrderByDescending(p => p.Priority).ToList();
        VisionProvider = providers.FirstOrDefault(p => p.Source == PerceptionSource.Vision);
        _monitor = monitor;
        _usage = usage;
        _log = log;
        if (_monitor is not null) { _monitor.Changed += OnUiChanged; }
    }

    public IPerceptionProvider? VisionProvider { get; }

    /// <summary>Maximum age of a cached snapshot when no change events arrive.</summary>
    public TimeSpan MaxCacheAge { get; set; } = TimeSpan.FromSeconds(20);

    public PerceptionRequest DefaultRequest { get; set; } = new();

    public int InvalidationCount { get; private set; }

    private void OnUiChanged(object? sender, UiChangedEventArgs e)
    {
        if (e.Kind == UiChangeKind.Values) { return; } // value changes are tracked by targeted read-back
        Invalidate(e.WindowHandle);
    }

    public void Invalidate(nint window)
    {
        // A new generation makes results of captures that started before the change unusable.
        _generation.AddOrUpdate(window, 1, (_, g) => g + 1);
        _prefetch.TryRemove(window, out _);
        if (_cache.TryRemove(window, out _))
        {
            InvalidationCount++;
            _log.Debug("perception", $"cache invalidated for 0x{window:X}");
        }
    }

    public void InvalidateAll()
    {
        foreach (var key in _cache.Keys.Concat(_prefetch.Keys).Distinct().ToList()) { Invalidate(key); }
    }

    private long Generation(nint window) => _generation.GetValueOrDefault(window);

    /// <summary>Starts capturing in the background (called the moment the overlay opens).</summary>
    public void Prefetch(WindowInfo window)
    {
        if (_cache.ContainsKey(window.Handle) || _prefetch.ContainsKey(window.Handle)) { return; }
        var task = Task.Run(() => CaptureBestAsync(window, DefaultRequest, CancellationToken.None));
        _prefetch[window.Handle] = task;
        _ = task.ContinueWith(t => ((ICollection<KeyValuePair<nint, Task<UiSnapshot?>>>)_prefetch).Remove(new(window.Handle, task)), TaskScheduler.Default);
    }

    public UiSnapshot? TryGetCached(nint window) => _cache.TryGetValue(window, out var entry) ? entry.Snapshot : null;

    public async Task<UiSnapshot?> GetSnapshotAsync(WindowInfo window, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _cache.TryGetValue(window.Handle, out var cached) && DateTimeOffset.UtcNow - cached.CapturedAt < MaxCacheAge)
        {
            return cached.Snapshot;
        }

        if (!forceRefresh && _prefetch.TryGetValue(window.Handle, out var pending))
        {
            try
            {
                var prefetched = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (prefetched is not null) { return prefetched; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("perception", $"prefetch failed: {ex.GetType().Name}");
            }
        }

        return await CaptureBestAsync(window, DefaultRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UiSnapshot?> CaptureBestAsync(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken)
    {
        var generation = Generation(window.Handle);
        UiSnapshot? best = null;
        foreach (var provider in _providers)
        {
            if (!provider.CanHandle(window)) { continue; }
            var sw = Stopwatch.StartNew();
            try
            {
                using (_usage.Measure($"perception.{provider.Source}"))
                {
                    var snapshot = await provider.CaptureAsync(window, request, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    if (snapshot is null) { continue; }
                    snapshot = snapshot with { CaptureDuration = sw.Elapsed };
                    _log.Info("perception", $"{provider.Source} elements={snapshot.Elements.Count} ms={sw.ElapsedMilliseconds}");
                    if (IsUseful(snapshot))
                    {
                        Store(window.Handle, snapshot, generation);
                        return snapshot;
                    }
                    best ??= snapshot;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("perception", $"{provider.Source} failed after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (best is not null) { Store(window.Handle, best, generation); }
        return best;
    }

    /// <summary>A snapshot is useful when it contains at least one interactive element.</summary>
    public static bool IsUseful(UiSnapshot snapshot) =>
        snapshot.Elements.Any(e => e.IsEditable || e.IsClickable);

    /// <summary>Whether the vision fallback should be considered for this snapshot.</summary>
    public static bool NeedsVision(UiSnapshot? snapshot) =>
        snapshot is null || snapshot.Elements.Count(e => e.IsEditable || e.IsClickable) < 2;

    private void Store(nint window, UiSnapshot snapshot, long generation)
    {
        // Only cache results that are still current (no invalidation happened while capturing).
        if (Generation(window) != generation) { return; }
        _cache[window] = new CacheEntry(snapshot, Interlocked.Increment(ref _version), DateTimeOffset.UtcNow);
    }

    /// <summary>Stores a snapshot produced elsewhere (e.g. the vision analyzer).</summary>
    public void Put(UiSnapshot snapshot) => Store(snapshot.Window.Handle, snapshot, Generation(snapshot.Window.Handle));

    /// <summary>
    /// Updates the cached snapshot after Kairo itself changed a value, so the next step does not need to re-read the UI.
    /// </summary>
    public void ApplyLocalChange(nint window, int elementId, string? newValue, bool? isChecked)
    {
        if (!_cache.TryGetValue(window, out var entry)) { return; }
        var updated = entry.Snapshot.WithElementUpdated(elementId, e => e with
        {
            Value = newValue ?? e.Value,
            IsChecked = isChecked ?? e.IsChecked,
        });
        _cache[window] = entry with { Snapshot = updated };
    }

    /// <summary>Targeted read-back for verification (no full snapshot).</summary>
    public async Task<IReadOnlyDictionary<string, ElementState>> ReadStatesAsync(UiSnapshot snapshot, IReadOnlyCollection<UiElement> elements, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.Source == snapshot.Source) ?? (snapshot.Source == PerceptionSource.Vision ? VisionProvider : null);
        if (provider is null || elements.Count == 0) { return new Dictionary<string, ElementState>(); }
        using (_usage.Measure($"verify.readback.{snapshot.Source}"))
        {
            return await provider.ReadStatesAsync(snapshot, elements, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts/stops event based invalidation for the task's target window.</summary>
    public void Watch(WindowInfo window) => _monitor?.Watch(window);

    public void StopWatching() => _monitor?.StopWatching();
}
