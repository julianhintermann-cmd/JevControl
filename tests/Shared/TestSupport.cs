using System.Diagnostics;
using System.Windows.Threading;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Kairo.Windows.Windows;

namespace Kairo.Tests.Shared;

/// <summary>Helpers for tests that drive real Windows applications.</summary>
public static class TestSupport
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kairo.sln"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException("Kairo.sln not found above " + AppContext.BaseDirectory);
    }

    public static string TestTargetExe()
    {
        var root = Path.Combine(RepoRoot(), "tests", "Kairo.TestTargetApp", "bin");
        var exe = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "Kairo.TestTargetApp.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return exe ?? throw new FileNotFoundException("Kairo.TestTargetApp.exe not built. Build the solution first.");
    }

    /// <summary>Starts the native test form and waits for its window.</summary>
    public static async Task<(Process Process, WindowInfo Window, string OutputFile)> StartTestTargetAsync(TimeSpan? timeout = null)
    {
        var output = Path.Combine(Path.GetTempPath(), "kairo-target-" + Guid.NewGuid().ToString("N") + ".json");
        var process = Process.Start(new ProcessStartInfo(TestTargetExe(), $"--out \"{output}\"") { UseShellExecute = false })!;
        var window = await WaitForWindowAsync(w => w.ProcessId == process.Id && w.Title.StartsWith("Kairo Testformular", StringComparison.Ordinal), timeout ?? TimeSpan.FromSeconds(30));
        return (process, window, output);
    }

    public static async Task<WindowInfo> WaitForWindowAsync(Func<WindowInfo, bool> predicate, TimeSpan timeout)
    {
        var windows = new WindowService(KairoLogger.Null);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var match = windows.ListWindows().FirstOrDefault(predicate);
            if (match is not null) { return match; }
            await Task.Delay(150);
        }
        throw new TimeoutException("Window did not appear in time.");
    }

    public static void Kill(Process? process)
    {
        if (process is null) { return; }
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    /// <summary>Runs code on a dedicated STA thread with a WPF dispatcher (hotkeys, windows, clipboard).</summary>
    public static Task<T> RunStaAsync<T>(Func<Dispatcher, Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { tcs.SetResult(await work(dispatcher)); }
                catch (Exception ex) { tcs.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Lets the dispatcher process pending messages (layout, input) for a moment.</summary>
    public static async Task PumpAsync(int milliseconds = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(15);
        }
    }

    /// <summary>
    /// UI tests need an interactive desktop session (GitHub-hosted Windows runners have one). When input cannot be
    /// injected (e.g. locked session), tests that depend on it are skipped with a clear reason instead of failing.
    /// </summary>
    public static bool IsInteractiveSession() => Environment.UserInteractive;
}
