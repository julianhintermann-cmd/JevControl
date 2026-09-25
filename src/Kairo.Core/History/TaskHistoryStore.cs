using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kairo.Core.Agent;
using Kairo.Core.Telemetry;

namespace Kairo.Core.History;

/// <summary>Encrypts/decrypts local data blobs (Windows: DPAPI CurrentUser).</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Used where no protector is available (tests, non-Windows). Stores plain bytes.</summary>
public sealed class NoOpProtector : IDataProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>Persisted summary of a finished task (no field values, no screenshots).</summary>
public sealed record TaskHistoryRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Instruction { get; init; }
    public string State { get; init; } = "";
    public string? Result { get; init; }
    public string? Application { get; init; }
    public int Actions { get; init; }
    public int PlanningRounds { get; init; }
    public int ModelCalls { get; init; }
    public int DecisionCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public double DurationMs { get; init; }
    public bool ScreenshotSent { get; init; }
    public Dictionary<string, double> LatencyMs { get; init; } = [];

    public static TaskHistoryRecord FromTask(AgentTask task, bool storeInstruction) => new()
    {
        Id = task.Id,
        CreatedAt = task.CreatedAt,
        FinishedAt = task.FinishedAt,
        Instruction = storeInstruction ? task.Instruction : null,
        State = task.State.ToString(),
        Result = Redactor.Redact(task.ResultMessage ?? task.ErrorMessage),
        Application = task.TargetWindow?.ProcessName,
        Actions = task.CompletedActions,
        PlanningRounds = task.PlanningRounds,
        ModelCalls = task.Metrics.ModelCalls,
        DecisionCalls = task.Metrics.DecisionCalls,
        InputTokens = task.Metrics.TotalInputTokens,
        OutputTokens = task.Metrics.TotalOutputTokens,
        Cost = task.Metrics.TotalCost,
        DurationMs = task.Duration.TotalMilliseconds,
        ScreenshotSent = task.ScreenshotSent,
        LatencyMs = task.Metrics.LatencySummary.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value.TotalMs, 1)),
    };
}

/// <summary>Task history, encrypted with DPAPI (via <see cref="IDataProtector"/>), with retention.</summary>
public sealed class TaskHistoryStore
{
    private const int MaxRecords = 500;
    private readonly string _file;
    private readonly IDataProtector _protector;
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private List<TaskHistoryRecord>? _records;

    public TaskHistoryStore(string directory, IDataProtector protector, KairoLogger log)
    {
        _file = Path.Combine(directory, "history.dat");
        _protector = protector;
        _log = log;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<TaskHistoryRecord> GetAll()
    {
        lock (_lock)
        {
            _records ??= Load();
            return _records.OrderByDescending(r => r.CreatedAt).ToList();
        }
    }

    public void Add(TaskHistoryRecord record, int retentionDays)
    {
        lock (_lock)
        {
            _records ??= Load();
            _records.RemoveAll(r => r.Id == record.Id);
            _records.Add(record);
            var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, retentionDays));
            _records.RemoveAll(r => r.CreatedAt < cutoff);
            if (_records.Count > MaxRecords) { _records = _records.OrderByDescending(r => r.CreatedAt).Take(MaxRecords).ToList(); }
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records = [];
            SecureDelete(_file);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public decimal TotalCost(DateTimeOffset since)
    {
        lock (_lock)
        {
            _records ??= Load();
            return _records.Where(r => r.CreatedAt >= since).Sum(r => r.Cost);
        }
    }

    private List<TaskHistoryRecord> Load()
    {
        try
        {
            if (!File.Exists(_file)) { return []; }
            var bytes = _protector.Unprotect(File.ReadAllBytes(_file));
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(bytes), HistoryJsonContext.Default.ListTaskHistoryRecord) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _log.Warn("history", $"history could not be loaded: {ex.GetType().Name}");
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var json = JsonSerializer.Serialize(_records, HistoryJsonContext.Default.ListTaskHistoryRecord);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(Encoding.UTF8.GetBytes(json)));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("history", $"history could not be saved: {ex.GetType().Name}");
        }
    }

    /// <summary>Overwrites a file with random bytes before deleting it.</summary>
    public static void SecureDelete(string file)
    {
        try
        {
            if (!File.Exists(file)) { return; }
            var length = new FileInfo(file).Length;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[Math.Min(length, 64 * 1024)];
                long written = 0;
                while (written < length)
                {
                    System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
                    var n = (int)Math.Min(buffer.Length, length - written);
                    fs.Write(buffer, 0, n);
                    written += n;
                }
                fs.Flush(true);
            }
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<TaskHistoryRecord>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
