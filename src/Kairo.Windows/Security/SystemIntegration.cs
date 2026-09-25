using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kairo.Core.Files;
using Kairo.Core.Telemetry;
using Microsoft.Win32;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Security;

/// <summary>Optional start with Windows (HKCU\...\Run, no admin rights needed).</summary>
public sealed class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kairo";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>The executable the Run value points to (without arguments), or null.</summary>
    public string? GetTargetPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string command) { return null; }
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) { key.SetValue(ValueName, $"\"{executablePath}\" --background"); }
        else if (key.GetValue(ValueName) is not null) { key.DeleteValue(ValueName, throwOnMissingValue: false); }
    }
}

/// <summary>
/// Registers the native messaging host "com.kairo.bridge" for Chrome and Edge (per user).
/// The installer writes the same keys; this class repairs them for portable/dev runs.
/// </summary>
public sealed class NativeHostRegistrar
{
    public const string HostName = "com.kairo.bridge";
    public const string ExtensionId = "fjdcafkellelfdkneebdlmoggkhkilmh";

    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
        @"Software\Chromium\NativeMessagingHosts\" + HostName,
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
    ];

    /// <summary>Writes the host manifest next to the host exe and points the browser registry keys to it.</summary>
    public static string Register(string hostExecutablePath, string manifestDirectory)
    {
        Directory.CreateDirectory(manifestDirectory);
        var manifestPath = Path.Combine(manifestDirectory, HostName + ".json");
        var manifest = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "Kairo Browser Bridge",
            ["path"] = hostExecutablePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new JsonArray($"chrome-extension://{ExtensionId}/"),
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (var keyPath in BrowserKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(null, manifestPath);
        }
        return manifestPath;
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(BrowserKeys[0]);
        return key?.GetValue(null) is string path && File.Exists(path);
    }

    public static void Unregister()
    {
        foreach (var keyPath in BrowserKeys)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}

/// <summary>Deletes files by moving them to the Windows recycle bin (never permanently).</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    public bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }
}

/// <summary>Clipboard access on a dedicated STA thread (works from any caller thread).</summary>
public sealed class ClipboardService
{
    public Task<bool> SetTextAsync(string text) => RunSta(() =>
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50); // clipboard temporarily locked by another app
            }
        }
        return false;
    });

    public Task<string?> GetTextAsync() => RunSta<string?>(() =>
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    });

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Kairo clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}

/// <summary>Removes all Kairo user data (settings, history, logs, secrets) – optionally overwriting files first.</summary>
public static class UserDataCleaner
{
    public static void RemoveAll(Kairo.Core.Settings.KairoPaths paths, KairoLogger log)
    {
        foreach (var dir in paths.AllDataDirectories)
        {
            if (!Directory.Exists(dir)) { continue; }
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
            {
                Kairo.Core.History.TaskHistoryStore.SecureDelete(file);
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException ex) { log.Warn("cleanup", $"folder not removed: {ex.GetType().Name}"); }
            catch (UnauthorizedAccessException ex) { log.Warn("cleanup", $"folder not removed: {ex.GetType().Name}"); }
        }
    }
}
