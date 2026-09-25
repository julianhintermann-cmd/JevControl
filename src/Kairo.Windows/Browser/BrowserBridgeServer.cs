using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Kairo.Shared;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Browser;

public sealed class BrowserBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Event sent by the extension (tabActivated, tabUpdated, navigationCompleted, domChanged, …).</summary>
public sealed record BrowserEvent(BrowserConnection Connection, string Event, int? TabId, int? WindowId, string? Url);

/// <summary>One connected browser (one native messaging host process).</summary>
public sealed class BrowserConnection
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly Stream _stream;
    private long _nextId;

    internal BrowserConnection(Stream stream, string browser, string version)
    {
        _stream = stream;
        Browser = browser;
        Version = version;
    }

    /// <summary>"chrome", "edge" or "chromium".</summary>
    public string Browser { get; }
    public string Version { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    public bool IsOpen { get; internal set; } = true;

    public BrowserKind Kind => Browser switch
    {
        "edge" => BrowserKind.Edge,
        "chrome" => BrowserKind.Chrome,
        _ => BrowserKind.OtherChromium,
    };

    /// <summary>Actions on a tab must not interleave – callers use this lock.</summary>
    public SemaphoreSlim ActionLock => _actionLock;

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsOpen) { throw new BrowserBridgeException("disconnected", "Die Browser-Erweiterung ist nicht mehr verbunden."); }
        var id = "k" + Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new JsonObject
            {
                ["type"] = "request",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            };
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BridgeProtocol.WriteMessageAsync(_stream, message.ToJsonString(), BridgeProtocol.MaxToBrowser, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new BrowserBridgeException("timeout", $"Die Browser-Erweiterung hat nicht rechtzeitig geantwortet ({method}).");
        }
        catch (IOException)
        {
            IsOpen = false;
            throw new BrowserBridgeException("disconnected", "Die Verbindung zur Browser-Erweiterung wurde getrennt.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal void Complete(JsonNode message)
    {
        var id = message["id"]?.ToString();
        if (id is null || !_pending.TryRemove(id, out var tcs)) { return; }
        if (message["ok"]?.GetValue<bool>() == true)
        {
            tcs.TrySetResult(message["result"]);
        }
        else
        {
            var code = message["error"]?["code"]?.ToString() ?? "error";
            var text = message["error"]?["message"]?.ToString() ?? "Unbekannter Fehler der Browser-Erweiterung.";
            tcs.TrySetException(new BrowserBridgeException(code, text));
        }
    }

    internal void FailAll()
    {
        IsOpen = false;
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new BrowserBridgeException("disconnected", "Die Verbindung zur Browser-Erweiterung wurde getrennt."));
        }
        _pending.Clear();
    }
}

/// <summary>
/// Named pipe server for Kairo.BrowserHost (native messaging relay). Security:
/// the pipe is created with PipeOptions.CurrentUserOnly and an ACL for the current user only, and the
/// connecting process must be Kairo.BrowserHost.exe from Kairo's installation directory.
/// </summary>
public sealed class BrowserBridgeServer : IAsyncDisposable
{
    private readonly KairoLogger _log;
    private readonly string _pipeName;
    private readonly string? _expectedHostPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<BrowserConnection, byte> _connections = new();
    private Task? _acceptLoop;

    /// <param name="expectedHostPath">Full path of Kairo.BrowserHost.exe; null disables the check (tests).</param>
    public BrowserBridgeServer(KairoLogger log, string? expectedHostPath, string? pipeName = null)
    {
        _log = log;
        _expectedHostPath = expectedHostPath;
        _pipeName = pipeName ?? BridgeProtocol.PipeName();
    }

    public string PipeName => _pipeName;

    public event EventHandler<BrowserConnection>? Connected;
    public event EventHandler<BrowserConnection>? Disconnected;
    public event EventHandler<BrowserEvent>? EventReceived;

    public IReadOnlyList<BrowserConnection> Connections => _connections.Keys.Where(c => c.IsOpen).OrderByDescending(c => c.ConnectedAt).ToList();

    public BrowserConnection? ConnectionFor(BrowserKind kind) =>
        Connections.FirstOrDefault(c => c.Kind == kind) ?? (kind == BrowserKind.OtherChromium ? Connections.FirstOrDefault() : null);

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024, security);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        _log.Info("bridge", "browser bridge listening");
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("bridge", "pipe creation failed", ex);
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (!IsTrustedClient(pipe))
            {
                _log.Warn("bridge", "rejected pipe client (unexpected process)");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(pipe, cancellationToken), cancellationToken);
        }
    }

    private bool IsTrustedClient(NamedPipeServerStream pipe)
    {
        if (_expectedHostPath is null) { return true; }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) { return false; }
        var path = GetProcessPath((int)pid);
        return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_expectedHostPath), StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        BrowserConnection? connection = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var text = await BridgeProtocol.ReadMessageAsync(pipe, BridgeProtocol.MaxFromBrowser, cancellationToken).ConfigureAwait(false);
                if (text is null) { break; }
                JsonNode? message;
                try { message = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { continue; }
                if (message is null) { continue; }

                switch (message["type"]?.ToString())
                {
                    case "hello":
                        connection = new BrowserConnection(pipe, message["browser"]?.ToString() ?? "chromium", message["extensionVersion"]?.ToString() ?? "?");
                        _connections[connection] = 0;
                        _log.Info("bridge", $"browser connected: {connection.Browser} (extension {connection.Version})");
                        Connected?.Invoke(this, connection);
                        break;
                    case "response":
                        connection?.Complete(message);
                        break;
                    case "event" when connection is not null:
                        EventReceived?.Invoke(this, new BrowserEvent(connection, message["event"]?.ToString() ?? "",
                            TryInt(message["tabId"]), TryInt(message["windowId"]), message["url"]?.ToString()));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (connection is not null)
            {
                connection.FailAll();
                _connections.TryRemove(connection, out _);
                _log.Info("bridge", $"browser disconnected: {connection.Browser}");
                Disconnected?.Invoke(this, connection);
            }
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int? TryInt(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var c in _connections.Keys) { c.FailAll(); }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
