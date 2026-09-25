using System.Runtime.InteropServices;
using static Kairo.Windows.Interop.NativeMethods;

namespace Kairo.Windows.Input;

/// <summary>A parsed key combination such as "ctrl+shift+t".</summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key, bool ExtendedKey)
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["eingabe"] = 0x0D,
        ["tab"] = 0x09, ["tabulator"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["leertaste"] = 0x20, ["leer"] = 0x20,
        ["backspace"] = 0x08, ["rücktaste"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["entf"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D, ["einfg"] = 0x2D,
        ["home"] = 0x24, ["pos1"] = 0x24, ["end"] = 0x23, ["ende"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["bildauf"] = 0x21,
        ["pagedown"] = 0x22, ["pgdn"] = 0x22, ["bildab"] = 0x22,
        ["left"] = 0x25, ["links"] = 0x25, ["up"] = 0x26, ["hoch"] = 0x26, ["oben"] = 0x26,
        ["right"] = 0x27, ["rechts"] = 0x27, ["down"] = 0x28, ["runter"] = 0x28, ["unten"] = 0x28,
        ["printscreen"] = 0x2C, ["druck"] = 0x2C, ["apps"] = 0x5D, ["menu"] = 0x5D, ["kontextmenü"] = 0x5D,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    private static readonly Dictionary<string, ushort> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VK_CONTROL, ["control"] = VK_CONTROL, ["strg"] = VK_CONTROL,
        ["shift"] = VK_SHIFT, ["umschalt"] = VK_SHIFT,
        ["alt"] = VK_MENU,
        ["win"] = VK_LWIN, ["windows"] = VK_LWIN, ["meta"] = VK_LWIN, ["cmd"] = VK_LWIN,
    };

    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5D];

    public static KeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { throw new FormatException("Leere Tastenkombination."); }
        var parts = text.Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (text.Trim().EndsWith("++", StringComparison.Ordinal)) { parts = [.. parts, "plus"]; }
        var modifiers = new List<ushort>();
        ushort? key = null;
        foreach (var part in parts)
        {
            if (ModifierNames.TryGetValue(part, out var mod))
            {
                if (!modifiers.Contains(mod)) { modifiers.Add(mod); }
                continue;
            }
            if (key is not null) { throw new FormatException($"Mehr als eine Haupttaste in „{text}“."); }
            key = ResolveKey(part);
        }

        if (key is null)
        {
            // Only modifiers ("win") → press the last modifier as key.
            if (modifiers.Count == 0) { throw new FormatException($"Unbekannte Tastenkombination „{text}“."); }
            key = modifiers[^1];
            modifiers.RemoveAt(modifiers.Count - 1);
        }
        return new KeyChord(modifiers, key.Value, Extended.Contains(key.Value));
    }

    private static ushort ResolveKey(string part)
    {
        if (Named.TryGetValue(part, out var vk)) { return vk; }
        if (part.Length == 1)
        {
            var c = part[0];
            if (c is >= 'a' and <= 'z') { return (ushort)char.ToUpperInvariant(c); }
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { return c; }
            var scan = VkKeyScanEx(c, GetKeyboardLayout(0));
            if (scan != -1) { return (ushort)(scan & 0xFF); }
        }
        throw new FormatException($"Unbekannte Taste „{part}“.");
    }
}

/// <summary>
/// Keyboard and mouse simulation via SendInput – only used when no structured interface
/// (UI Automation pattern, DOM) is available or when correcting a failed step.
/// </summary>
public sealed class InputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void PressChord(KeyChord chord)
    {
        var inputs = new List<INPUT>();
        foreach (var m in chord.Modifiers) { inputs.Add(Key(m, false, m == VK_LWIN)); }
        inputs.Add(Key(chord.Key, false, chord.ExtendedKey));
        inputs.Add(Key(chord.Key, true, chord.ExtendedKey));
        for (var i = chord.Modifiers.Count - 1; i >= 0; i--) { inputs.Add(Key(chord.Modifiers[i], true, chord.Modifiers[i] == VK_LWIN)); }
        Send(inputs);
    }

    public void Press(string chord) => PressChord(KeyChord.Parse(chord));

    /// <summary>
    /// Types text as Unicode key events (layout independent). Newlines become Enter only when
    /// <paramref name="allowNewlines"/> is set (multi-line fields), otherwise a space – Enter could submit a form.
    /// </summary>
    public async Task TypeTextAsync(string text, bool allowNewlines, int delayMs, Func<bool> mayContinue, CancellationToken cancellationToken)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        const int chunk = 24;
        var batch = new List<INPUT>(chunk * 2);
        var count = 0;
        foreach (var c in normalized)
        {
            if (c == '\n')
            {
                if (allowNewlines)
                {
                    batch.Add(Key(VK_RETURN, false, false));
                    batch.Add(Key(VK_RETURN, true, false));
                }
                else
                {
                    AddUnicode(batch, ' ');
                }
            }
            else if (c == '\t')
            {
                AddUnicode(batch, ' ');
            }
            else
            {
                AddUnicode(batch, c);
            }

            count++;
            if (delayMs > 0 || batch.Count >= chunk * 2)
            {
                if (!mayContinue()) { throw new OperationCanceledException(); }
                Send(batch);
                batch.Clear();
                if (delayMs > 0) { await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false); }
                else if (count % 200 == 0) { await Task.Yield(); }
            }
        }
        if (batch.Count > 0)
        {
            if (!mayContinue()) { throw new OperationCanceledException(); }
            Send(batch);
        }
    }

    private static void AddUnicode(List<INPUT> batch, char c)
    {
        var down = new INPUT { type = INPUT_KEYBOARD };
        down.U.ki.wScan = c;
        down.U.ki.dwFlags = KEYEVENTF_UNICODE;
        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        batch.Add(down);
        batch.Add(up);
    }

    public void MoveMouse(int x, int y)
    {
        var (nx, ny) = Normalize(x, y);
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dx = nx;
        input.U.mi.dy = ny;
        input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        Send([input]);
    }

    public void Click(int x, int y, string button = "left", int clicks = 1)
    {
        var (down, up) = button.ToLowerInvariant() switch
        {
            "right" or "rechts" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" or "mitte" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        var (nx, ny) = Normalize(x, y);
        var inputs = new List<INPUT>();
        var move = new INPUT { type = INPUT_MOUSE };
        move.U.mi.dx = nx;
        move.U.mi.dy = ny;
        move.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        inputs.Add(move);
        for (var i = 0; i < Math.Clamp(clicks, 1, 3); i++)
        {
            var d = new INPUT { type = INPUT_MOUSE };
            d.U.mi.dwFlags = down;
            var u = new INPUT { type = INPUT_MOUSE };
            u.U.mi.dwFlags = up;
            inputs.Add(d);
            inputs.Add(u);
        }
        Send(inputs);
    }

    /// <summary>Mouse wheel; positive = up.</summary>
    public void Wheel(int x, int y, int notches)
    {
        MoveMouse(x, y);
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.mouseData = notches * 120;
        input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
        Send([input]);
    }

    private static (int X, int Y) Normalize(int x, int y)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return ((int)Math.Round((x - left) * 65535.0 / (width - 1)), (int)Math.Round((y - top) * 65535.0 / (height - 1)));
    }

    private static INPUT Key(ushort vk, bool up, bool extended)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wVk = vk;
        input.U.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        input.U.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        return input;
    }

    private static void Send(IReadOnlyList<INPUT> inputs)
    {
        if (inputs.Count == 0) { return; }
        var array = inputs as INPUT[] ?? inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, InputSize);
        if (sent != array.Length)
        {
            // UIPI blocks input into elevated windows when Kairo runs without elevation.
            throw new InvalidOperationException("Windows hat die simulierte Eingabe blockiert (z. B. ein als Administrator laufendes Fenster).");
        }
    }

    /// <summary>True while the user physically holds a modifier – simulated chords would mix with it.</summary>
    public static bool UserHoldsModifier() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
}
