using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.History;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;

namespace Kairo.Core.Tests;

public class InfrastructureTests
{
    [Fact]
    public void Settings_roundtrip_and_never_contain_secrets()
    {
        using var dir = new TempDir();
        var paths = new KairoPaths(dir.File("roaming"), dir.File("local"));
        var store = new SettingsStore(paths, KairoLogger.Null);
        store.Update(s =>
        {
            s.Models.PlannerModel = "anthropic/claude-haiku-4.5";
            s.Hotkeys.Overlay = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Space");
            s.Privacy.VisionConsent = VisionConsent.Ask;
            s.Control.MaxActionsPerTask = 100000; // clamped
        });

        var reloaded = new SettingsStore(paths, KairoLogger.Null).Current;
        Assert.Equal("anthropic/claude-haiku-4.5", reloaded.Models.PlannerModel);
        Assert.Equal("Space", reloaded.Hotkeys.Overlay.Key);
        Assert.Equal(VisionConsent.Ask, reloaded.Privacy.VisionConsent);
        Assert.Equal(500, reloaded.Control.MaxActionsPerTask);
        var json = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain("sk-or", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"visionConsent\": \"Ask\"", json);
    }

    [Fact]
    public void Corrupted_settings_fall_back_to_defaults_and_keep_a_backup()
    {
        using var dir = new TempDir();
        var paths = new KairoPaths(dir.File("roaming"), dir.File("local"));
        Directory.CreateDirectory(paths.RoamingDirectory);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");
        var store = new SettingsStore(paths, KairoLogger.Null);
        Assert.Equal(HotkeyBinding.DefaultOverlay, store.Current.Hotkeys.Overlay);
        Assert.Single(Directory.GetFiles(paths.RoamingDirectory, "settings.json.broken-*"));
    }

    [Fact]
    public void Execution_gate_cannot_reopen_and_reacts_to_pause()
    {
        var control = new ComputerControlSwitch();
        using var gate = new ExecutionGate(default, control);
        Assert.True(gate.IsOpen);
        control.SetPaused(true);
        Assert.False(gate.IsOpen);
        Assert.Throws<OperationCanceledException>(gate.ThrowIfClosed);
        control.SetPaused(false);
        Assert.False(gate.IsOpen); // stays closed for this task
        Assert.Equal("Computersteuerung pausiert", gate.ClosedReason);
    }

    [Fact]
    public void Loop_guard_stops_repetitions()
    {
        var guard = new LoopGuard(maxRepeats: 2);
        var a = new AgentAction { Kind = ActionKind.Hotkey, Keys = "ctrl+s" };
        guard.Register(a, null);
        guard.Register(a, null);
        Assert.Throws<LoopDetectedException>(() => guard.Register(a, null));
        guard.RoundFinished(false);
        guard.RoundFinished(false);
        Assert.Throws<LoopDetectedException>(() => guard.RoundFinished(false));
    }

    [Theory]
    [InlineData("max@example.ch", " max@example.ch ", true)]
    [InlineData("+41 79 123 45 67", "+41791234567", true)]
    [InlineData("079 123 45 67", "(079) 123-45-67", true)]
    [InlineData("Max", "Moritz", false)]
    [InlineData("Zeile 1\nZeile 2", "Zeile 1\r\nZeile 2", true)]
    [InlineData("", "", true)]
    [InlineData("x", "", false)]
    public void Value_comparison_is_tolerant_to_formatting(string expected, string observed, bool match)
    {
        Assert.Equal(match, Verifier.ValuesMatch(expected, observed));
    }

    [Fact]
    public void Snapshot_formatter_never_prints_password_values_and_masks_vault_values()
    {
        var vault = new Kairo.Core.Security.SecretVault();
        var snapshot = new UiSnapshot
        {
            SnapshotId = "s",
            Source = PerceptionSource.BrowserDom,
            Window = new WindowInfo { Handle = 1, Title = "Login", ProcessName = "msedge" },
            Url = "https://example.com/login",
            Elements =
            [
                new UiElement { Id = 1, Role = ElementRole.Edit, Name = "Passwort", Value = "hunter2", IsPassword = true, Locator = "a", Capabilities = ElementCapabilities.SetValue },
                new UiElement { Id = 2, Role = ElementRole.Edit, Name = "Karte", Value = "4111 1111 1111 1111", Locator = "b", Capabilities = ElementCapabilities.SetValue, IsRequired = true },
                new UiElement { Id = 3, Role = ElementRole.ComboBox, Name = "Land", Value = "Schweiz", Options = ["Schweiz", "Deutschland"], Locator = "c", Capabilities = ElementCapabilities.ExpandCollapse },
            ],
            TextBlocks = ["Anmelden"],
        };
        var text = SnapshotFormatter.Format(snapshot, vault);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("[1] edit \"Passwort\" password (gefüllt)", text);
        Assert.DoesNotContain("4111 1111 1111 1111", text);
        Assert.Contains("required", text);
        Assert.Contains("options=[Schweiz, Deutschland]", text);
        Assert.Contains("Browser-DOM", text);
    }

    [Fact]
    public async Task History_is_protected_and_securely_cleared()
    {
        using var dir = new TempDir();
        var protector = new XorProtector();
        var store = new TaskHistoryStore(dir.Path, protector, KairoLogger.Null);
        store.Add(new TaskHistoryRecord { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.Now, Instruction = "geheime Anweisung", Cost = 0.01m }, 30);
        store.Add(new TaskHistoryRecord { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.Now.AddDays(-60), Instruction = "alt" }, 30);

        var raw = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "history.dat"));
        Assert.DoesNotContain("geheime", System.Text.Encoding.UTF8.GetString(raw));
        var reloaded = new TaskHistoryStore(dir.Path, protector, KairoLogger.Null).GetAll();
        Assert.Single(reloaded); // retention removed the old entry
        Assert.Equal(0.01m, store.TotalCost(DateTimeOffset.Now.AddDays(-1)));

        store.Clear();
        Assert.False(File.Exists(Path.Combine(dir.Path, "history.dat")));
    }

    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    [Fact]
    public void Logger_redacts_and_keeps_recent_entries()
    {
        using var dir = new TempDir();
        using var log = new KairoLogger(dir.Path);
        log.Info("test", "calling with sk-or-v1-abcdefabcdefabcdefabcdef");
        Assert.Contains(log.Recent, e => e.Message.Contains("sk-or-***"));
        log.Dispose();
        var file = Directory.GetFiles(dir.Path, "kairo-*.log").Single();
        Assert.DoesNotContain("abcdefabcdef", File.ReadAllText(file));
    }

    [Fact]
    public async Task Perception_cache_is_reused_until_invalidated()
    {
        var desktop = Fakes.FakeDesktop.ContactForm();
        var monitor = new FakeMonitor();
        var perception = new PerceptionService([desktop], monitor, new UsageTracker(KairoLogger.Null), KairoLogger.Null);
        perception.Prefetch(desktop.Window);
        var a = await perception.GetSnapshotAsync(desktop.Window, false, CancellationToken.None);
        var b = await perception.GetSnapshotAsync(desktop.Window, false, CancellationToken.None);
        Assert.Same(a, b);
        Assert.Equal(1, desktop.Captures);

        monitor.Raise(new UiChangedEventArgs(desktop.Window.Handle, UiChangeKind.Values));
        var c = await perception.GetSnapshotAsync(desktop.Window, false, CancellationToken.None);
        Assert.Same(a, c); // value changes don't invalidate

        monitor.Raise(new UiChangedEventArgs(desktop.Window.Handle, UiChangeKind.Structure));
        await perception.GetSnapshotAsync(desktop.Window, false, CancellationToken.None);
        Assert.Equal(2, desktop.Captures);

        perception.ApplyLocalChange(desktop.Window.Handle, 1, "Max", null);
        Assert.Equal("Max", perception.TryGetCached(desktop.Window.Handle)!.Find(1)!.Value);
    }

    private sealed class FakeMonitor : IUiChangeMonitor
    {
        public event EventHandler<UiChangedEventArgs>? Changed;
        public void Raise(UiChangedEventArgs e) => Changed?.Invoke(this, e);
        public void Watch(WindowInfo window) { }
        public void StopWatching() { }
    }
}
