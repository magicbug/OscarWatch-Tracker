using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OscarWatch.Views;

public partial class SstvSettingsWindow : Window
{
    public SstvSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
