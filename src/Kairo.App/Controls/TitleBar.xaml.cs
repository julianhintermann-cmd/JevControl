using System.Windows;
using System.Windows.Controls;

namespace Kairo.App.Controls;

/// <summary>Custom Windows 11 style title bar for windows with WindowChrome (Mica backdrop).</summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("Kairo", (d, e) => ((TitleBar)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty CanMaximizeProperty =
        DependencyProperty.Register(nameof(CanMaximize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true, (d, e) =>
        {
            var bar = (TitleBar)d;
            bar.MaximizeButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            bar.MinimizeButton.Visibility = bar.MaximizeButton.Visibility;
        }));

    public TitleBar()
    {
        InitializeComponent();
        TitleText.Text = Title;
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window is not null) { window.StateChanged += (_, _) => UpdateMaximizeGlyph(window); }
        };
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool CanMaximize
    {
        get => (bool)GetValue(CanMaximizeProperty);
        set => SetValue(CanMaximizeProperty, value);
    }

    private void UpdateMaximizeGlyph(Window window) =>
        MaximizeButton.Content = window.WindowState == WindowState.Maximized ? "" : "";

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(Window.GetWindow(this)!);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this)!;
        if (window.WindowState == WindowState.Maximized) { SystemCommands.RestoreWindow(window); }
        else { SystemCommands.MaximizeWindow(window); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
