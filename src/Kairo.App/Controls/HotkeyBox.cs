using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kairo.Core.Settings;

namespace Kairo.App.Controls;

/// <summary>Captures a key combination (modifier + key) when focused. Used for custom hotkeys.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(HotkeyBinding), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).UpdateText()));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) => Text = "Tastenkombination drücken …";
        LostKeyboardFocus += (_, _) => UpdateText();
        SetResourceReference(StyleProperty, typeof(TextBox));
    }

    public HotkeyBinding? Hotkey
    {
        get => (HotkeyBinding?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private void UpdateText() => Text = Hotkey?.ToString() ?? "";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { modifiers |= HotkeyModifiers.Control; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { modifiers |= HotkeyModifiers.Alt; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { modifiers |= HotkeyModifiers.Shift; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { modifiers |= HotkeyModifiers.Windows; }
        if (modifiers == HotkeyModifiers.None || modifiers == HotkeyModifiers.Shift)
        {
            Text = "Bitte mit Strg, Alt oder Win kombinieren …";
            return;
        }

        var name = KeyName(key);
        if (name is null)
        {
            Text = "Diese Taste wird nicht unterstützt …";
            return;
        }
        Hotkey = new HotkeyBinding(modifiers, name);
        Keyboard.ClearFocus();
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F12 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        _ => null,
    };
}
