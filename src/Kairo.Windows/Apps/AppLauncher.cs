using System.Diagnostics;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Telemetry;
using Kairo.Windows.Windows;
using Microsoft.Win32;

namespace Kairo.Windows.Apps;

/// <summary>
/// Starts programs by friendly name ("Excel", "Rechner", "Editor"), path, App Paths registration or Start menu
/// shortcut, then waits (event-like polling, 100 ms) for the new window so it becomes the next target.
/// </summary>
public sealed class AppLauncher
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["editor"] = "notepad.exe", ["notepad"] = "notepad.exe", ["texteditor"] = "notepad.exe",
        ["rechner"] = "calc.exe", ["calculator"] = "calc.exe", ["taschenrechner"] = "calc.exe",
        ["explorer"] = "explorer.exe", ["datei-explorer"] = "explorer.exe", ["dateiexplorer"] = "explorer.exe", ["file explorer"] = "explorer.exe",
        ["paint"] = "mspaint.exe",
        ["chrome"] = "chrome.exe", ["google chrome"] = "chrome.exe",
        ["edge"] = "msedge.exe", ["microsoft edge"] = "msedge.exe",
        ["firefox"] = "firefox.exe",
        ["word"] = "winword.exe", ["microsoft word"] = "winword.exe",
        ["excel"] = "excel.exe", ["microsoft excel"] = "excel.exe",
        ["powerpoint"] = "powerpnt.exe",
        ["outlook"] = "outlook.exe",
        ["onenote"] = "onenote.exe",
        ["teams"] = "ms-teams:", ["microsoft teams"] = "ms-teams:",
        ["einstellungen"] = "ms-settings:", ["settings"] = "ms-settings:", ["windows-einstellungen"] = "ms-settings:",
        ["terminal"] = "wt.exe", ["windows terminal"] = "wt.exe",
        ["eingabeaufforderung"] = "cmd.exe", ["cmd"] = "cmd.exe",
        ["powershell"] = "powershell.exe",
        ["task-manager"] = "taskmgr.exe", ["taskmanager"] = "taskmgr.exe",
        ["systemsteuerung"] = "control.exe", ["control panel"] = "control.exe",
        ["snipping tool"] = "snippingtool.exe", ["kalender"] = "outlookcal:", ["mail"] = "outlookmail:",
    };

    private readonly WindowService _windows;
    private readonly KairoLogger _log;

    public AppLauncher(WindowService windows, KairoLogger log)
    {
        _windows = windows;
        _log = log;
    }

    public sealed record LaunchResult(bool Success, string Message, WindowInfo? Window);

    public async Task<LaunchResult> LaunchAsync(string app, string? arguments, CancellationToken cancellationToken)
    {
        var target = Resolve(app);
        if (target is null) { return new LaunchResult(false, $"Programm „{app}“ wurde nicht gefunden.", null); }

        // An already running instance of a single-window app is simply activated.
        var before = _windows.ListWindows().Select(w => w.Handle).ToHashSet();
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) { psi.Arguments = arguments; }
            using var process = Process.Start(psi);
            _log.Info("apps", $"started {Path.GetFileName(target)}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new LaunchResult(false, $"„{app}“ konnte nicht gestartet werden: {ex.Message}", null);
        }

        var window = await WaitForNewWindowAsync(before, app, target, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (window is not null)
        {
            await _windows.ActivateAsync(window.Handle, cancellationToken).ConfigureAwait(false);
            return new LaunchResult(true, $"{window.DisplayName} geöffnet.", window);
        }
        return new LaunchResult(true, $"„{app}“ gestartet (kein neues Fenster erkannt).", _windows.GetForegroundWindow());
    }

    private async Task<WindowInfo?> WaitForNewWindowAsync(HashSet<nint> before, string app, string target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exeName = Path.GetFileNameWithoutExtension(target);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var fresh = _windows.ListWindows().Where(w => !before.Contains(w.Handle)).ToList();
            var match = fresh.FirstOrDefault(w => w.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                        ?? fresh.FirstOrDefault(w => w.Title.Contains(app, StringComparison.OrdinalIgnoreCase))
                        ?? (fresh.Count == 1 ? fresh[0] : null);
            if (match is not null) { return match; }
        }

        // Single-instance apps (e.g. Explorer, Teams) may reuse an existing window.
        return _windows.FindWindow(app);
    }

    /// <summary>Resolves a friendly name, path or URI to something ShellExecute can start.</summary>
    public static string? Resolve(string app)
    {
        var name = app.Trim().Trim('"');
        if (name.Length == 0) { return null; }
        if (name.Contains("://", StringComparison.Ordinal) || (name.EndsWith(':') && !name.Contains('\\'))) { return name; }
        if (Path.IsPathRooted(name)) { return File.Exists(name) || Directory.Exists(name) ? name : null; }
        if (Aliases.TryGetValue(name, out var alias))
        {
            return alias.EndsWith(':') ? alias : FromAppPaths(alias) ?? alias;
        }

        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        return FromAppPaths(exe) ?? FromStartMenu(name) ?? FromPath(exe);
    }

    private static string? FromAppPaths(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) { return path.Trim('"'); }
        }
        return null;
    }

    private static string? FromPath(string exe)
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
        if (File.Exists(system)) { return system; }
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
        if (File.Exists(windows)) { return windows; }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) { return candidate; }
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Searches Start menu shortcuts (user and all users) by display name.</summary>
    private static string? FromStartMenu(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        var wanted = ElementMatcher.Normalize(name);
        string? best = null;
        var bestScore = 0.0;
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).ToList(); }
            catch (IOException) { continue; }
            foreach (var link in links)
            {
                var display = ElementMatcher.Normalize(Path.GetFileNameWithoutExtension(link));
                if (display.Contains("uninstall") || display.Contains("deinstall")) { continue; }
                var score = display == wanted ? 1.0 : ElementMatcher.TextSimilarity(wanted, display);
                if (score > bestScore) { best = link; bestScore = score; }
            }
        }
        return bestScore >= 0.75 ? best : null;
    }
}
