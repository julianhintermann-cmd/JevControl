using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Kairo.TestTargetApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, object?>
        {
            ["Vorname"] = FirstName.Text,
            ["Nachname"] = LastName.Text,
            ["E-Mail"] = Email.Text,
            ["Telefon"] = Phone.Text,
            ["Firma"] = Company.Text,
            ["Land"] = (Country.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            ["Nachricht"] = Message.Text,
            ["Datenschutz"] = Privacy.IsChecked == true,
            ["Newsletter"] = Newsletter.IsChecked == true,
        };
        if (App.OutputPath is { } path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(values));
        }
        Status.Text = "Vielen Dank! Ihre Nachricht wurde gesendet.";
    }
}
