using System.Windows;
using Kairo.App.Services;
using Kairo.App.ViewModels;

namespace Kairo.App.Views;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _vm;

    public OnboardingWindow(OnboardingViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        theme.Track(this, BackdropKind.Mica);
        viewModel.Completed += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            // PasswordBox.Password is not bindable: keep it in sync when the key is revealed/hidden or cleared.
            if (e.PropertyName == nameof(OnboardingViewModel.RevealKey) && !viewModel.RevealKey && KeyBox.Password != viewModel.ApiKey)
            {
                KeyBox.Password = viewModel.ApiKey;
            }
            if (e.PropertyName == nameof(OnboardingViewModel.ApiKey) && viewModel.ApiKey.Length == 0 && KeyBox.Password.Length > 0)
            {
                KeyBox.Password = "";
            }
        };
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiKey != KeyBox.Password) { _vm.ApiKey = KeyBox.Password; }
    }
}
