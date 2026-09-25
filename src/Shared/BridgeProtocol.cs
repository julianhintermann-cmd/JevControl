using System.Buffers.Binary;
using System.Text;

namespace Kairo.Shared;

/// <summary>
/// Framing shared by the Kairo desktop app and Kairo.BrowserHost (Chrome native messaging uses the same format):
/// 4-byte little-endian length followed by UTF-8 JSON.
/// </summary>
internal static class BridgeProtocol
{
    /// <summary>Messages from Kairo to the browser are limited to 1 MB by Chrome.</summary>
    public const int MaxToBrowser = 1024 * 1024;

    /// <summary>Messages from the browser may be up to 64 MB.</summary>
    public const int MaxFromBrowser = 64 * 1024 * 1024;

    /// <summary>Per-user pipe name: kairo-browser-bridge-&lt;user name, lower case, [a-z0-9] only&gt;.</summary>
    public static string PipeName(string? userName = null)
    {
        var user = (userName ?? Environment.UserName).ToLowerInvariant();
        var safe = new string(user.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
        return "kairo-browser-bridge-" + (safe.Length == 0 ? "user" : safe);
    }

    public static async Task<string?> ReadMessageAsync(Stream stream, int maxLength, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) { return null; }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxLength) { throw new InvalidDataException($"Invalid message length {length}."); }
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false)) { return null; }
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteMessageAsync(Stream stream, string json, int maxLength, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > maxLength) { throw new InvalidDataException($"Message too large ({body.Length} bytes)."); }
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) { return false; }
            offset += read;
        }
        return true;
    }
}
