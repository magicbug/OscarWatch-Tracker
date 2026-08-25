using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.Core.Models;
using OscarWatch.Localization;

namespace OscarWatch.Views;

public partial class ScheduledPassAlertWindow : Window
{
    public ScheduledPassAlertWindow()
    {
        InitializeComponent();
    }

    public ScheduledPassAlertWindow(
        string satelliteName,
        string countdown,
        string aosText,
        PassPolarPlotData? plotData = null,
        double minimumElevationDeg = 5,
        HorizonMask? horizonMask = null,
        bool useUtcTime = false,
        bool use24HourClock = true)
        : this()
    {
        var l = LocalizationService.Instance;
        SatelliteNameText.Text = satelliteName;
        CountdownText.Text = l.Get("Pass.Schedule.AlertCountdown", countdown);
        AosTimeText.Text = l.Get("Pass.Schedule.AlertAosAt", aosText);
        AutomationProperties.SetName(
            this,
            l.Get("Pass.Schedule.AlertMessage", satelliteName, countdown, aosText));

        if (plotData is null)
        {
            PolarPlotHost.IsVisible = false;
            Width = 420;
            MinWidth = 360;
            return;
        }

        PolarPlot.PlotData = plotData;
        PolarPlot.MinimumElevationDeg = minimumElevationDeg;
        PolarPlot.HorizonMask = horizonMask;
        PolarPlot.UseUtcTime = useUtcTime;
        PolarPlot.Use24HourClock = use24HourClock;
        PolarPlotHost.IsVisible = true;
        Width = 640;
        MinWidth = 560;
    }

    public static void Show(
        Window? owner,
        string satelliteName,
        string countdown,
        string aosText,
        PassPolarPlotData? plotData = null,
        double minimumElevationDeg = 5,
        HorizonMask? horizonMask = null,
        bool useUtcTime = false,
        bool use24HourClock = true)
    {
        var window = new ScheduledPassAlertWindow(
            satelliteName,
            countdown,
            aosText,
            plotData,
            minimumElevationDeg,
            horizonMask,
            useUtcTime,
            use24HourClock)
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
