using System.Security.Cryptography;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Tests.Shared;
using Kairo.Windows.Hotkeys;
using Kairo.Windows.Input;
using Kairo.Windows.Security;

namespace Kairo.Windows.Tests;

public class SecretStoreTests
{
    [SkippableFact]
    public void Api_key_is_stored_encrypted_and_can_be_reloaded()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows only.");
        var dir = Path.Combine(Path.GetTempPath(), "kairo-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string key = "sk-or-v1-0123456789abcdef0123456789abcdef0123456789abcdef";
            var store = new DpapiSecretStore(dir, KairoLogger.Null);
            Assert.False(store.HasSecret("openrouter-api-key"));
            store.SetSecret("openrouter-api-key", key);

            var file = Path.Combine(dir, "openrouter-api-key.dpapi");
            Assert.True(File.Exists(file));
            var raw = File.ReadAllBytes(file);
            Assert.DoesNotContain("sk-or-v1", System.Text.Encoding.UTF8.GetString(raw));
            Assert.DoesNotContain("sk-or-v1", System.Text.Encoding.Unicode.GetString(raw));

            // A new instance (like after a restart) reads the same key.
            var reloaded = new DpapiSecretStore(dir, KairoLogger.Null);
            Assert.Equal(key, reloaded.GetSecret("openrouter-api-key"));

            // Without the per-install entropy the blob cannot be decrypted.
            Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));

            reloaded.DeleteSecret("openrouter-api-key");
            Assert.False(reloaded.HasSecret("openrouter-api-key"));
            Assert.Null(reloaded.GetSecret("openrouter-api-key"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [SkippableFact]
    public void History_protector_roundtrips()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var dir = Path.Combine(Path.GetTempPath(), "kairo-dpapi-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiSecretStore(dir, KairoLogger.Null);
        var data = System.Text.Encoding.UTF8.GetBytes("Verlauf");
        var protectedData = store.Protect(data);
        Assert.NotEqual(data, protectedData);
        Assert.Equal(data, store.Unprotect(protectedData));
        store.DeleteAll();
    }
}

public class HotkeyTests
{
    private static readonly HotkeyBinding TestBinding = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F11");

    [SkippableFact]
    public async Task Registers_global_hotkey_and_detects_conflicts()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var (first, second, afterRelease) = await TestSupport.RunStaAsync(async dispatcher =>
        {
            using var a = new GlobalHotkeyManager(KairoLogger.Null);
            using var b = new GlobalHotkeyManager(KairoLogger.Null);
            var r1 = a.Register(TestBinding, () => { }, out var id);
            var r2 = b.Register(TestBinding, () => { }, out _);
            a.Unregister(id);
            var r3 = b.Probe(TestBinding);
            await Task.Yield();
            return (r1, r2, r3);
        });
        Assert.Equal(HotkeyRegistrationResult.Registered, first);
        Assert.Equal(HotkeyRegistrationResult.AlreadyInUse, second);
        Assert.Equal(HotkeyRegistrationResult.Registered, afterRelease);
    }

    [SkippableFact]
    public async Task Pressing_the_hotkey_invokes_the_callback()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession(), "Needs an interactive desktop for SendInput.");
        var fired = await TestSupport.RunStaAsync(async dispatcher =>
        {
            using var manager = new GlobalHotkeyManager(KairoLogger.Null);
            var tcs = new TaskCompletionSource<bool>();
            Assert.Equal(HotkeyRegistrationResult.Registered, manager.Register(TestBinding, () => tcs.TrySetResult(true), out _));
            new InputSimulator().Press("ctrl+alt+shift+f11");
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            return winner == tcs.Task;
        });
        Assert.True(fired, "WM_HOTKEY was not received after SendInput.");
    }

    [Fact]
    public void Default_hotkeys_are_valid()
    {
        Assert.True(GlobalHotkeyManager.TryConvert(HotkeyBinding.DefaultOverlay, out var mods, out var vk));
        Assert.Equal((uint)'K', vk);
        Assert.Equal(0x4000u | 0x0001u | 0x0002u, mods);
        Assert.False(GlobalHotkeyManager.TryConvert(new HotkeyBinding(HotkeyModifiers.None, "K"), out _, out _));
    }
}

public class InputTests
{
    [Theory]
    [InlineData("ctrl+s", 2, 'S')]
    [InlineData("Strg+Umschalt+T", 2, 'T')]
    [InlineData("alt+f4", 1, 0x73)]
    [InlineData("enter", 0, 0x0D)]
    [InlineData("win+r", 1, 'R')]
    public void Parses_key_chords(string text, int modifierCount, int key)
    {
        var chord = KeyChord.Parse(text);
        Assert.Equal(modifierCount, chord.Modifiers.Count);
        Assert.Equal(key, chord.Key);
    }

    [Fact]
    public void Rejects_invalid_chords()
    {
        Assert.Throws<FormatException>(() => KeyChord.Parse(""));
        Assert.Throws<FormatException>(() => KeyChord.Parse("ctrl+a+b"));
        Assert.Throws<FormatException>(() => KeyChord.Parse("ctrl+unbekannt"));
    }
}

public class ClipboardTests
{
    [SkippableFact]
    public async Task Clipboard_roundtrip()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var clipboard = new ClipboardService();
        var text = "Kairo Test " + Guid.NewGuid();
        Assert.True(await clipboard.SetTextAsync(text));
        Assert.Equal(text, await clipboard.GetTextAsync());
    }
}
