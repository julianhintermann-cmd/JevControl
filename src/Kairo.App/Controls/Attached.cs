using System.Windows;
using System.Windows.Controls;

namespace Kairo.App.Controls;

/// <summary>Attached helpers used by the control templates.</summary>
public static class Ui
{
    /// <summary>Placeholder text for TextBox/PasswordBox/ComboBox.</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));

    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string value) => o.SetValue(PlaceholderProperty, value);

    /// <summary>Segoe Fluent / MDL2 glyph shown by buttons and nav items.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));

    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string value) => o.SetValue(IconProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(4)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);

    /// <summary>True while a PasswordBox contains text (PasswordBox.Password is not bindable).</summary>
    public static readonly DependencyProperty HasTextProperty =
        DependencyProperty.RegisterAttached("HasText", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetHasText(DependencyObject o) => (bool)o.GetValue(HasTextProperty);
    public static void SetHasText(DependencyObject o, bool value) => o.SetValue(HasTextProperty, value);

    /// <summary>Tracks PasswordBox content for the placeholder.</summary>
    public static readonly DependencyProperty TrackPasswordProperty =
        DependencyProperty.RegisterAttached("TrackPassword", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnTrackPasswordChanged));

    public static bool GetTrackPassword(DependencyObject o) => (bool)o.GetValue(TrackPasswordProperty);
    public static void SetTrackPassword(DependencyObject o, bool value) => o.SetValue(TrackPasswordProperty, value);

    private static void OnTrackPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) { return; }
        box.PasswordChanged -= OnPasswordChanged;
        if ((bool)e.NewValue) { box.PasswordChanged += OnPasswordChanged; }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SetHasText(box, box.SecurePassword.Length > 0);
    }
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points (both fonts share them).</summary>
public static class Glyphs
{
    public const string Settings = "";
    public const string Cancel = "";
    public const string CheckMark = "";
    public const string Send = "";
    public const string Pause = "";
    public const string Play = "";
    public const string History = "";
    public const string Info = "";
    public const string Shield = "";
    public const string Lock = "";
    public const string Keyboard = "";
    public const string Globe = "";
    public const string Robot = "";
    public const string Camera = "";
    public const string Folder = "";
    public const string Delete = "";
    public const string Refresh = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Completed = "";
    public const string Color = "";
    public const string Permissions = "";
    public const string Code = "";
    public const string Link = "";
    public const string View = "";
    public const string Hide = "";
    public const string Mouse = "";
    public const string Home = "";
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string Add = "";
    public const string Remove = "";
    public const string Help = "";
    public const string Bolt = "";
    public const string Minimize = "";
    public const string Maximize = "";
    public const string Restore = "";
    public const string Close = "";
    public const string Privacy = "";
    public const string Document = "";
    public const string Money = "";
}
