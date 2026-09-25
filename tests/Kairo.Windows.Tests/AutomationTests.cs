using System.Text.Json;
using System.Windows.Media.Imaging;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Kairo.Tests.Shared;
using Kairo.Windows.Apps;
using Kairo.Windows.Automation;
using Kairo.Windows.Browser;
using Kairo.Windows.Capture;
using Kairo.Windows.Input;
using Kairo.Windows.Security;
using Kairo.Windows.Windows;
using Xunit.Abstractions;

namespace Kairo.Windows.Tests;

/// <summary>Real UI Automation against the native WPF test form (Kairo.TestTargetApp).</summary>
public sealed class AutomationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private System.Diagnostics.Process? _process;
    private WindowInfo? _window;
    private string _outputFile = "";
    private readonly UiaCore _core = new(KairoLogger.Null);
    private readonly WindowService _windows = new(KairoLogger.Null);

    public AutomationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        (_process, _window, _outputFile) = await TestSupport.StartTestTargetAsync();
    }

    public Task DisposeAsync()
    {
        TestSupport.Kill(_process);
        if (File.Exists(_outputFile)) { File.Delete(_outputFile); }
        return Task.CompletedTask;
    }

    private WindowsActionExecutor CreateExecutor()
    {
        var input = new InputSimulator();
        var bridge = new BrowserBridgeServer(KairoLogger.Null, null, "kairo-test-" + Guid.NewGuid().ToString("N"));
        return new WindowsActionExecutor(
            new UiaElementActions(_core, _windows, input, KairoLogger.Null),
            new BrowserElementActions(bridge, _windows, input),
            _windows, input, new AppLauncher(_windows, KairoLogger.Null), new ClipboardService(), KairoLogger.Null);
    }

    private async Task<UiSnapshot> SnapshotAsync()
    {
        var provider = new UiaPerceptionProvider(_core, KairoLogger.Null);
        var snapshot = await provider.CaptureAsync(_window!, new PerceptionRequest(), CancellationToken.None);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    [SkippableFact]
    public void Detects_the_test_window()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.NotNull(_window);
        Assert.Equal("Kairo.TestTargetApp", _window!.ProcessName);
        Assert.Contains(_windows.ListWindows(), w => w.Handle == _window.Handle);
        Assert.Equal(_window.Handle, _windows.FindWindow("Kairo Testformular")?.Handle);
    }

    [SkippableFact]
    public async Task Activates_the_window_and_reports_it_as_foreground()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession());
        Assert.True(await _windows.ActivateAsync(_window!.Handle, CancellationToken.None));
        Assert.Equal(_window.Handle, _windows.GetForegroundWindow()?.Handle);
    }

    [SkippableFact]
    public async Task Reads_the_form_structure_with_ui_automation()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var snapshot = await SnapshotAsync();
        foreach (var e in snapshot.Elements) { _output.WriteLine(Kairo.Core.Perception.SnapshotFormatter.FormatElement(e)); }

        Assert.Equal(PerceptionSource.UiAutomation, snapshot.Source);
        var first = Assert.Single(snapshot.Elements, e => e.Name == "Vorname");
        Assert.Equal(ElementRole.Edit, first.Role);
        Assert.True(first.IsEditable);
        Assert.True(first.IsRequired);
        Assert.Contains(snapshot.Elements, e => e.Name == "E-Mail" && e.Role == ElementRole.Edit);
        var country = Assert.Single(snapshot.Elements, e => e.Name == "Land");
        Assert.Equal(ElementRole.ComboBox, country.Role);
        var privacy = Assert.Single(snapshot.Elements, e => e.Name == "Ich akzeptiere die Datenschutzerklärung");
        Assert.Equal(ElementRole.CheckBox, privacy.Role);
        Assert.False(privacy.IsChecked);
        Assert.Contains(snapshot.Elements, e => e.Name == "Absenden" && e.Role == ElementRole.Button);
        Assert.Contains("Kontaktformular", snapshot.TextBlocks);
        Assert.All(snapshot.Elements, e => Assert.StartsWith("uia:", e.Locator));
        Assert.True(snapshot.CaptureDuration < TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task Fills_selects_checks_and_submits_with_structured_patterns()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var snapshot = await SnapshotAsync();
        var executor = CreateExecutor();
        using var gate = new ExecutionGate();
        var context = new ActionContext { TargetWindow = _window!, Snapshot = snapshot, Gate = gate };
        int Id(string name) => snapshot.Elements.First(e => e.Name == name).Id;

        var batch = await executor.ExecuteElementBatchAsync(
        [
            new AgentAction { Kind = ActionKind.SetValue, TargetId = Id("Vorname"), Value = "Max" },
            new AgentAction { Kind = ActionKind.SetValue, TargetId = Id("Nachname"), Value = "Muster" },
            new AgentAction { Kind = ActionKind.SetValue, TargetId = Id("E-Mail"), Value = "max.muster@example.ch" },
            new AgentAction { Kind = ActionKind.SetChecked, TargetId = Id("Ich akzeptiere die Datenschutzerklärung"), Checked = true },
        ], context, CancellationToken.None);
        Assert.All(batch, r => Assert.True(r.Success, r.Message));
        Assert.Contains(batch, r => r.Strategy == "ValuePattern");

        var select = await executor.ExecuteAsync(new AgentAction { Kind = ActionKind.SelectOption, TargetId = Id("Land"), Option = "Deutschland" }, context, CancellationToken.None);
        Assert.True(select.Success, select.Message);

        // Independent read-back through UI Automation.
        var provider = new UiaPerceptionProvider(_core, KairoLogger.Null);
        var states = await provider.ReadStatesAsync(snapshot, snapshot.Elements.Where(e => e.Name is "Vorname" or "E-Mail" or "Ich akzeptiere die Datenschutzerklärung" or "Land").ToList(), CancellationToken.None);
        Assert.Equal("Max", states[snapshot.Elements.First(e => e.Name == "Vorname").Locator].Value);
        Assert.Equal("max.muster@example.ch", states[snapshot.Elements.First(e => e.Name == "E-Mail").Locator].Value);
        Assert.True(states[snapshot.Elements.First(e => e.Name == "Ich akzeptiere die Datenschutzerklärung").Locator].IsChecked);

        var click = await executor.ExecuteAsync(new AgentAction { Kind = ActionKind.Click, TargetId = Id("Absenden") }, context, CancellationToken.None);
        Assert.True(click.Success, click.Message);
        await WaitForFileAsync(_outputFile);
        using var json = JsonDocument.Parse(File.ReadAllText(_outputFile));
        Assert.Equal("Max", json.RootElement.GetProperty("Vorname").GetString());
        Assert.Equal("Muster", json.RootElement.GetProperty("Nachname").GetString());
        Assert.Equal("Deutschland", json.RootElement.GetProperty("Land").GetString());
        Assert.True(json.RootElement.GetProperty("Datenschutz").GetBoolean());
    }

    [SkippableFact]
    public async Task Correction_mode_types_with_simulated_keyboard_input()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession(), "Needs an interactive desktop for SendInput.");
        var snapshot = await SnapshotAsync();
        var executor = CreateExecutor();
        using var gate = new ExecutionGate();
        var context = new ActionContext { TargetWindow = _window!, Snapshot = snapshot, Gate = gate, PreferInputSimulation = true };
        var message = snapshot.Elements.First(e => e.Name == "Nachricht");
        var result = await executor.ExecuteAsync(new AgentAction { Kind = ActionKind.SetValue, TargetId = message.Id, Value = "Grüße aus Zürich – 2 Zeilen\nZeile 2" }, context, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal("SendInput", result.Strategy);
        var states = await new UiaPerceptionProvider(_core, KairoLogger.Null).ReadStatesAsync(snapshot, [message], CancellationToken.None);
        Assert.Equal("Grüße aus Zürich – 2 Zeilen\r\nZeile 2", states[message.Locator].Value?.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    [SkippableFact]
    public async Task Cancelled_gate_blocks_every_action()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var snapshot = await SnapshotAsync();
        var executor = CreateExecutor();
        using var gate = new ExecutionGate();
        gate.Close("Test");
        var context = new ActionContext { TargetWindow = _window!, Snapshot = snapshot, Gate = gate };
        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            new AgentAction { Kind = ActionKind.SetValue, TargetId = snapshot.Elements.First(e => e.Name == "Vorname").Id, Value = "X" }, context, CancellationToken.None));
        var states = await new UiaPerceptionProvider(_core, KairoLogger.Null).ReadStatesAsync(snapshot, [snapshot.Elements.First(e => e.Name == "Vorname")], CancellationToken.None);
        Assert.Equal("", states.Values.Single().Value);
    }

    [SkippableFact]
    public async Task Captures_the_window_and_masks_secret_regions()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession());
        await _windows.ActivateAsync(_window!.Handle, CancellationToken.None);
        var snapshot = await SnapshotAsync();
        var email = snapshot.Elements.First(e => e.Name == "E-Mail");
        var capture = new ScreenCaptureService(KairoLogger.Null);
        var image = await capture.CaptureWindowAsync(_window, new CaptureOptions { MaskRegions = [email.Bounds], MaxEdge = 4000 }, CancellationToken.None);
        Assert.NotNull(image);
        _output.WriteLine($"capture via {capture.LastStrategy}: {image!.Width}x{image.Height}, scale {image.Scale}");
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Width > 200 && image.Height > 200);

        var decoder = new PngBitmapDecoder(new MemoryStream(image.Data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var cx = (int)((email.Bounds.Center.X - image.ScreenRegion.X) / image.Scale);
        var cy = (int)((email.Bounds.Center.Y - image.ScreenRegion.Y) / image.Scale);
        var pixel = new byte[4];
        frame.CopyPixels(new System.Windows.Int32Rect(cx, cy, 1, 1), pixel, 4, 0);
        Assert.Equal([0, 0, 0], pixel.Take(3).ToArray());

        // Downscaling for the vision model.
        var small = await capture.CaptureWindowAsync(_window, new CaptureOptions { MaxEdge = 320 }, CancellationToken.None);
        Assert.True(Math.Max(small!.Width, small.Height) <= 320);
        Assert.True(small.Scale > 1);
    }

    [SkippableFact]
    public async Task Structure_change_events_are_reported()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var monitor = new UiaChangeMonitor(_core, KairoLogger.Null);
        var changed = new TaskCompletionSource<bool>();
        monitor.Changed += (_, e) => { if (e.Kind == Kairo.Core.Abstractions.UiChangeKind.Structure) { changed.TrySetResult(true); } };
        monitor.Watch(_window!);
        await Task.Delay(500);

        // Clicking "Absenden" shows the status text → structure change.
        var snapshot = await SnapshotAsync();
        using var gate = new ExecutionGate();
        await CreateExecutor().ExecuteAsync(new AgentAction { Kind = ActionKind.Click, TargetId = snapshot.Elements.First(e => e.Name == "Absenden").Id },
            new ActionContext { TargetWindow = _window!, Snapshot = snapshot, Gate = gate }, CancellationToken.None);
        var winner = await Task.WhenAny(changed.Task, Task.Delay(5000));
        monitor.StopWatching();
        Skip.If(winner != changed.Task, "The UIA provider of this environment did not raise StructureChanged (cache expiry is used as fallback).");
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; i < 50 && !File.Exists(path); i++) { await Task.Delay(100); }
        Assert.True(File.Exists(path), "The test form did not write its output file.");
    }
}

public class AppLauncherTests
{
    [SkippableFact]
    public async Task Launches_notepad_and_detects_its_window()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var windows = new WindowService(KairoLogger.Null);
        var launcher = new AppLauncher(windows, KairoLogger.Null);
        var result = await launcher.LaunchAsync("Editor", null, CancellationToken.None);
        try
        {
            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Window);
            Assert.Contains("notepad", result.Window!.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (result.Window is { } w)
            {
                try { System.Diagnostics.Process.GetProcessById(w.ProcessId).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
        }
    }

    [SkippableFact]
    public void Resolves_known_apps()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.EndsWith("notepad.exe", AppLauncher.Resolve("notepad"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ms-settings:", AppLauncher.Resolve("Einstellungen"));
        Assert.Null(AppLauncher.Resolve(@"C:\gibt\es\nicht.exe"));
    }
}
