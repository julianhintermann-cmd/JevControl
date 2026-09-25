using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Kairo.App.Services;
using Kairo.App.ViewModels;

namespace Kairo.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        theme.Track(this, BackdropKind.Mica);

        // Auto-save (like Windows Settings): any edit schedules a debounced save.
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEdited));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextEdited));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelectionEdited));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(OnSliderEdited));
        AddHandler(LostFocusEvent, new RoutedEventHandler(OnEdited));

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.NewApiKey) && viewModel.NewApiKey.Length == 0) { KeyBox.Password = ""; }
            if (e.PropertyName == nameof(SettingsViewModel.RevealKey) && !viewModel.RevealKey) { KeyBox.Password = viewModel.NewApiKey; }
            if (e.PropertyName == nameof(SettingsViewModel.SelectedNav)) { PageScroller.ScrollToTop(); }
        };
        Activated += (_, _) => viewModel.RefreshStatus();
    }

    public void ShowPage(string key)
    {
        var entry = _vm.Navigation.FirstOrDefault(n => n.Key == key);
        if (entry is not null) { _vm.SelectedNav = entry; }
    }

    private void OnEdited(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListBoxItem or ListBox) { return; }
        _vm.ScheduleSave();
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox box && (box.IsKeyboardFocusWithin || box.TemplatedParent is ComboBox)) { _vm.ScheduleSave(); }
    }

    private void OnSelectionEdited(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is ComboBox) { _vm.ScheduleSave(); }
    }

    private void OnSliderEdited(object sender, RoutedPropertyChangedEventArgs<double> e) => _vm.ScheduleSave();

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_vm.NewApiKey != KeyBox.Password) { _vm.NewApiKey = KeyBox.Password; }
    }
}
