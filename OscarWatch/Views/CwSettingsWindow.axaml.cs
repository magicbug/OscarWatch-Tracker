using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OscarWatch.Views;

public partial class CwSettingsWindow : Window
{
    public CwSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
