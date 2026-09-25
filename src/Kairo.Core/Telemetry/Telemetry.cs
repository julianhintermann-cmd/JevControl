using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Kairo.Core.AI;

namespace Kairo.Core.Telemetry;

/// <summary>Removes secrets from any text that could reach logs, error messages or the UI.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"sk-or-(v\d+-)?[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenRouterKey();

    [GeneratedRegex(@"\b(sk|pk|rk)-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GenericKey();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(api[_-]?key|authorization|password|passwort|secret)(\s*[:=]\s*)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return text ?? ""; }
        text = OpenRouterKey().Replace(text, "sk-or-***");
        text = GenericKey().Replace(text, m => m.Value[..3] + "***");
        text = BearerToken().Replace(text, "Bearer ***");
        text = KeyValueSecret().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        return text;
    }
}

public enum KairoLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, KairoLogLevel Level, string Category, string Message);

/// <summary>
/// Minimal, privacy preserving logger. Callers log event names, durations and counts only –
/// never user content. Every message additionally passes the <see cref="Redactor"/>.
/// </summary>
public sealed class KairoLogger : IDisposable
{
    private readonly string? _directory;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _fileLock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public static KairoLogger Null { get; } = new(null);

    public KairoLogger(string? directory, KairoLogLevel minimumLevel = KairoLogLevel.Info)
    {
        _directory = directory;
        MinimumLevel = minimumLevel;
    }

    public KairoLogLevel MinimumLevel { get; set; }

    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>Last 300 entries (for the diagnostics page).</summary>
    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public void Debug(string category, string message) => Log(KairoLogLevel.Debug, category, message);
    public void Info(string category, string message) => Log(KairoLogLevel.Info, category, message);
    public void Warn(string category, string message) => Log(KairoLogLevel.Warning, category, message);

    public void Error(string category, string message, Exception? exception = null) =>
        Log(KairoLogLevel.Error, category, exception is null ? message : $"{message} [{Describe(exception)}]");

    /// <summary>
    /// Exception type, message and the top stack frames. Frames only name methods (no user content) and are what
    /// makes a crash report from a user's machine actionable.
    /// </summary>
    public static string Describe(Exception exception, int maxFrames = 8)
    {
        var frames = (exception.StackTrace ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(maxFrames);
        var text = $"{exception.GetType().FullName}: {exception.Message} | {string.Join(" | ", frames)}";
        return exception.InnerException is { } inner ? $"{text} ---> {Describe(inner, 4)}" : text;
    }

    public void Log(KairoLogLevel level, string category, string message)
    {
        if (level < MinimumLevel) { return; }
        var entry = new LogEntry(DateTimeOffset.Now, level, category, Redactor.Redact(message));
        _recent.Enqueue(entry);
        while (_recent.Count > 300 && _recent.TryDequeue(out _)) { }
        EntryWritten?.Invoke(this, entry);

        if (_directory is null) { return; }
        lock (_fileLock)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _writerDate != today)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    CleanupOldLogs(_directory);
                    _writer = new StreamWriter(new FileStream(Path.Combine(_directory, $"kairo-{today:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };
                    _writerDate = today;
                }
                _writer.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[..4]}] {category}: {entry.Message}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CleanupOldLogs(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "kairo-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) { File.Delete(file); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_fileLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Model usage of a single call.</summary>
public sealed record UsageRecord(DateTimeOffset Time, string Purpose, string Model, UsageInfo Usage, TimeSpan Latency);

/// <summary>Accumulated usage and latency of one task.</summary>
public sealed class TaskMetrics
{
    private readonly object _lock = new();
    private readonly List<UsageRecord> _calls = [];
    private readonly Dictionary<string, List<double>> _latencies = new(StringComparer.Ordinal);

    public IReadOnlyList<UsageRecord> Calls { get { lock (_lock) { return _calls.ToList(); } } }

    public void AddCall(UsageRecord record) { lock (_lock) { _calls.Add(record); } }

    public void AddLatency(string step, TimeSpan duration)
    {
        lock (_lock)
        {
            if (!_latencies.TryGetValue(step, out var list)) { _latencies[step] = list = []; }
            list.Add(duration.TotalMilliseconds);
        }
    }

    public IReadOnlyDictionary<string, (int Count, double TotalMs, double MaxMs)> LatencySummary
    {
        get
        {
            lock (_lock)
            {
                return _latencies.ToDictionary(kv => kv.Key, kv => (kv.Value.Count, kv.Value.Sum(), kv.Value.Max()));
            }
        }
    }

    public decimal TotalCost { get { lock (_lock) { return _calls.Sum(c => c.Usage.Cost ?? 0m); } } }
    public int TotalInputTokens { get { lock (_lock) { return _calls.Sum(c => c.Usage.InputTokens); } } }
    public int TotalOutputTokens { get { lock (_lock) { return _calls.Sum(c => c.Usage.OutputTokens); } } }
    public int ModelCalls { get { lock (_lock) { return _calls.Count; } } }
    public int DecisionCalls { get { lock (_lock) { return _calls.Count(c => c.Model.Contains("jev", StringComparison.OrdinalIgnoreCase)); } } }
}

/// <summary>
/// Records model usage (tokens, cost from OpenRouter's usage.cost) and step latencies.
/// Records flow into the metrics of the task that is currently executing (AsyncLocal scope).
/// </summary>
public sealed class UsageTracker
{
    private static readonly AsyncLocal<TaskMetrics?> CurrentScope = new();
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private readonly List<UsageRecord> _session = [];

    public UsageTracker(KairoLogger log) => _log = log;

    public TaskMetrics? Current => CurrentScope.Value;

    public IDisposable BeginTask(TaskMetrics metrics)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = metrics;
        return new Scope(() => CurrentScope.Value = previous);
    }

    public void Record(string purpose, string model, UsageInfo usage, TimeSpan latency)
    {
        var record = new UsageRecord(DateTimeOffset.Now, purpose, model, usage, latency);
        lock (_lock)
        {
            _session.Add(record);
            if (_session.Count > 2000) { _session.RemoveRange(0, 500); }
        }
        CurrentScope.Value?.AddCall(record);
        CurrentScope.Value?.AddLatency($"model.{purpose}", latency);
        _log.Info("usage", $"{purpose} model={model} in={usage.InputTokens} out={usage.OutputTokens} cost={usage.Cost?.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"} ms={latency.TotalMilliseconds:0}");
    }

    public decimal SessionCost { get { lock (_lock) { return _session.Sum(r => r.Usage.Cost ?? 0m); } } }

    /// <summary>Measures a processing step, e.g. <c>using (tracker.Measure("perception.uia"))</c>.</summary>
    public IDisposable Measure(string step)
    {
        var sw = Stopwatch.StartNew();
        var metrics = CurrentScope.Value;
        return new Scope(() =>
        {
            sw.Stop();
            metrics?.AddLatency(step, sw.Elapsed);
            _log.Debug("latency", $"{step} ms={sw.Elapsed.TotalMilliseconds:0.0}");
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
