using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Kairo.Core.Telemetry;
using Kairo.Tests.Shared;
using Kairo.Windows.Browser;

namespace Kairo.Windows.Tests;

/// <summary>
/// The real native messaging path without a browser: the test plays Chrome/Edge – it starts
/// Kairo.BrowserHost.exe with the extension origin, talks native messaging over stdin/stdout – and Kairo's
/// bridge server (named pipe, ACL, client process check) is on the other side.
/// </summary>
public class BrowserBridgeTests
{
    private const string ExtensionOrigin = "chrome-extension://fjdcafkellelfdkneebdlmoggkhkilmh/";
    private const string GeckoExtensionId = "kairo-bridge@jevcontrol";

    private static string? FindHost() =>
        Directory.EnumerateFiles(Path.Combine(TestSupport.RepoRoot(), "src", "Kairo.BrowserHost", "bin"), "Kairo.BrowserHost.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /// <summary>Command line as Chrome/Edge pass it: the caller's origin and the parent window.</summary>
    private static Process StartHost(string hostExe, string pipe, string origin) => StartHostWithArguments(hostExe, pipe, $"{origin} --parent-window=0");

    /// <summary>Command line as Firefox/Zen pass it: the host manifest path and the caller's add-on id.</summary>
    private static Process StartGeckoHost(string hostExe, string pipe, string addonId) =>
        StartHostWithArguments(hostExe, pipe, $"\"{Path.Combine(Path.GetDirectoryName(hostExe)!, "com.kairo.bridge.firefox.json")}\" {addonId}");

    private static Process StartHostWithArguments(string hostExe, string pipe, string arguments)
    {
        var psi = new ProcessStartInfo(hostExe, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["KAIRO_BRIDGE_PIPE"] = pipe;
        return Process.Start(psi)!;
    }

    private static async Task WriteNativeAsync(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<JsonNode?> ReadNativeAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(body, cts.Token);
        return JsonNode.Parse(body);
    }

    [SkippableFact]
    public async Task Native_host_relays_requests_and_responses_between_the_extension_and_kairo()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var hostExe = FindHost();
        Skip.If(hostExe is null, "Kairo.BrowserHost.exe not built.");

        var pipe = "kairo-test-" + Guid.NewGuid().ToString("N");
        using var log = new KairoLogger(null, KairoLogLevel.Debug);
        await using var server = new BrowserBridgeServer(log, hostExe, pipe);
        var connected = new TaskCompletionSource<BrowserConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Connected += (_, c) => connected.TrySetResult(c);
        server.Start();

        using var host = StartHost(hostExe!, pipe, ExtensionOrigin);
        try
        {
            var toHost = host.StandardInput.BaseStream;
            var fromHost = host.StandardOutput.BaseStream;
            await WriteNativeAsync(toHost, """{"type":"hello","browser":"edge","extensionVersion":"1.0.0"}""");

            var winner = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (winner != connected.Task)
            {
                string? hostSaid = null;
                try { hostSaid = (await ReadNativeAsync(fromHost, TimeSpan.FromSeconds(2)))?.ToJsonString(); }
                catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException) { }
                Assert.Fail($"The bridge did not report a connection from Kairo.BrowserHost.exe. Host exited: {host.HasExited}, host said: {hostSaid ?? "-"}, " +
                    $"server log: {string.Join(" | ", log.Recent.Select(e => $"{e.Category}: {e.Message}"))}");
            }
            var connection = await connected.Task;
            Assert.Equal("edge", connection.Browser);

            // Kairo → host → "browser"; the browser answers → host → Kairo.
            var request = connection.RequestAsync("ping", new JsonObject { ["probe"] = 42 }, TimeSpan.FromSeconds(10), CancellationToken.None);
            var received = await ReadNativeAsync(fromHost, TimeSpan.FromSeconds(10));
            Assert.Equal("request", received?["type"]?.ToString());
            Assert.Equal("ping", received?["method"]?.ToString());
            Assert.Equal(42, received?["params"]?["probe"]?.GetValue<int>());

            await WriteNativeAsync(toHost, new JsonObject
            {
                ["type"] = "response",
                ["id"] = received!["id"]!.ToString(),
                ["ok"] = true,
                ["result"] = new JsonObject { ["pong"] = true },
            }.ToJsonString());
            var result = await request;
            Assert.True(result?["pong"]?.GetValue<bool>());
        }
        finally
        {
            try { host.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [SkippableFact]
    public async Task Native_host_relays_for_the_firefox_and_zen_add_on()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var hostExe = FindHost();
        Skip.If(hostExe is null, "Kairo.BrowserHost.exe not built.");

        var pipe = "kairo-test-" + Guid.NewGuid().ToString("N");
        using var log = new KairoLogger(null, KairoLogLevel.Debug);
        await using var server = new BrowserBridgeServer(log, hostExe, pipe);
        var connected = new TaskCompletionSource<BrowserConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Connected += (_, c) => connected.TrySetResult(c);
        server.Start();

        using var host = StartGeckoHost(hostExe!, pipe, GeckoExtensionId);
        try
        {
            await WriteNativeAsync(host.StandardInput.BaseStream, """{"type":"hello","browser":"firefox","product":"Zen","extensionVersion":"1.0.2"}""");
            var winner = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(winner == connected.Task, $"no connection; host exited: {host.HasExited}, log: {string.Join(" | ", log.Recent.Select(e => e.Message))}");
            var connection = await connected.Task;
            Assert.Equal(Kairo.Core.Models.BrowserKind.Firefox, connection.Kind);
            Assert.Equal("Zen", connection.Product);

            var zen = new Kairo.Core.Models.WindowInfo { Handle = 1, Title = "Kontakt — Zen", ProcessName = "zen" };
            Assert.Same(connection, server.ConnectionFor(zen));
            var chrome = new Kairo.Core.Models.WindowInfo { Handle = 2, Title = "Kontakt – Google Chrome", ProcessName = "chrome" };
            Assert.Null(server.ConnectionFor(chrome));
        }
        finally
        {
            try { host.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [SkippableFact]
    public async Task Host_rejects_other_extensions_and_reports_a_missing_desktop_app()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var hostExe = FindHost();
        Skip.If(hostExe is null, "Kairo.BrowserHost.exe not built.");

        // A different extension id: the host exits immediately without relaying anything.
        using (var foreign = StartHost(hostExe!, "kairo-test-" + Guid.NewGuid().ToString("N"), "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"))
        {
            Assert.True(foreign.WaitForExit(10_000));
            Assert.Equal(2, foreign.ExitCode);
        }

        // The same for Firefox/Zen: another add-on id is rejected.
        using (var foreignGecko = StartGeckoHost(hostExe!, "kairo-test-" + Guid.NewGuid().ToString("N"), "someone-else@example.org"))
        {
            Assert.True(foreignGecko.WaitForExit(10_000));
            Assert.Equal(2, foreignGecko.ExitCode);
        }

        // Kairo is not running (nobody listens on the pipe): the extension is told so.
        using var host = StartHost(hostExe!, "kairo-test-" + Guid.NewGuid().ToString("N"), ExtensionOrigin);
        try
        {
            await WriteNativeAsync(host.StandardInput.BaseStream, """{"type":"hello","browser":"chrome","extensionVersion":"1.0.0"}""");
            var status = await ReadNativeAsync(host.StandardOutput.BaseStream, TimeSpan.FromSeconds(15));
            Assert.Equal("status", status?["type"]?.ToString());
            Assert.Equal("desktop_unavailable", status?["status"]?.ToString());
        }
        finally
        {
            try { host.Kill(); } catch (InvalidOperationException) { }
        }
    }
}
