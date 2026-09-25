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

    private static string? FindHost() =>
        Directory.EnumerateFiles(Path.Combine(TestSupport.RepoRoot(), "src", "Kairo.BrowserHost", "bin"), "Kairo.BrowserHost.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    private static Process StartHost(string hostExe, string pipe, string origin)
    {
        var psi = new ProcessStartInfo(hostExe, $"{origin} --parent-window=0")
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
        await using var server = new BrowserBridgeServer(KairoLogger.Null, hostExe, pipe);
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
            Assert.True(winner == connected.Task, "The bridge did not report a connection from Kairo.BrowserHost.exe.");
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
