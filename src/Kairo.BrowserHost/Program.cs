using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Kairo.Shared;

namespace Kairo.BrowserHost;

/// <summary>
/// Native messaging host started by Chrome/Edge for the Kairo extension. It only relays framed JSON messages
/// between the browser (stdin/stdout) and the Kairo desktop app (per-user named pipe). It never interprets or
/// stores message content.
/// </summary>
internal static class Program
{
    private const string ExtensionOrigin = "chrome-extension://fjdcafkellelfdkneebdlmoggkhkilmh/";

    private static async Task<int> Main(string[] args)
    {
        // Chrome passes the calling extension's origin as first argument (plus --parent-window on Windows).
        var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
        if (origin is not null && !string.Equals(origin, ExtensionOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        using var cts = new CancellationTokenSource();

        // Buffer browser messages (the extension sends "hello" immediately) until the pipe is connected.
        var fromBrowser = Channel.CreateUnbounded<string>();
        var readBrowser = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var message = await BridgeProtocol.ReadMessageAsync(stdin, BridgeProtocol.MaxFromBrowser, cts.Token).ConfigureAwait(false);
                    if (message is null) { break; }
                    await fromBrowser.Writer.WriteAsync(message, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
            finally
            {
                fromBrowser.Writer.TryComplete();
            }
        });

        var pipeName = Environment.GetEnvironmentVariable("KAIRO_BRIDGE_PIPE") is { Length: > 0 } overridePipe ? overridePipe : BridgeProtocol.PipeName();
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await BridgeProtocol.WriteMessageAsync(stdout, "{\"type\":\"status\",\"status\":\"desktop_unavailable\"}", BridgeProtocol.MaxToBrowser, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        var toDesktop = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in fromBrowser.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    await BridgeProtocol.WriteMessageAsync(pipe, message, BridgeProtocol.MaxFromBrowser, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
        });

        var toBrowser = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var message = await BridgeProtocol.ReadMessageAsync(pipe, BridgeProtocol.MaxToBrowser, cts.Token).ConfigureAwait(false);
                    if (message is null) { break; }
                    await BridgeProtocol.WriteMessageAsync(stdout, message, BridgeProtocol.MaxToBrowser, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
        });

        // Either side closing ends the relay (browser closed the port or Kairo exited).
        await Task.WhenAny(toDesktop, toBrowser, readBrowser).ConfigureAwait(false);
        if (toBrowser.IsCompleted && !readBrowser.IsCompleted)
        {
            // Kairo went away: tell the extension so it can retry later.
            try
            {
                await BridgeProtocol.WriteMessageAsync(stdout, "{\"type\":\"status\",\"status\":\"desktop_unavailable\"}", BridgeProtocol.MaxToBrowser, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException) { }
        }
        cts.Cancel();
        return 0;
    }
}
