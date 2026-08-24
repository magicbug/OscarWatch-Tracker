using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.Localization;

namespace OscarWatch.Views;

public partial class ScheduledPassAlertWindow : Window
{
    public ScheduledPassAlertWindow()
    {
        InitializeComponent();
    }

    public ScheduledPassAlertWindow(string satelliteName, string countdown, string aosText)
        : this()
    {
        var l = LocalizationService.Instance;
        SatelliteNameText.Text = satelliteName;
        CountdownText.Text = l.Get("Pass.Schedule.AlertCountdown", countdown);
        AosTimeText.Text = l.Get("Pass.Schedule.AlertAosAt", aosText);
        AutomationProperties.SetName(
            this,
            l.Get("Pass.Schedule.AlertMessage", satelliteName, countdown, aosText));
    }

    public static void Show(Window? owner, string satelliteName, string countdown, string aosText)
    {
        var window = new ScheduledPassAlertWindow(satelliteName, countdown, aosText)
        {
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        if (owner is not null)
            window.Show(owner);
        else
            window.Show();

        try
        {
            window.Activate();
        }
        catch
        {
            // best-effort focus
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
