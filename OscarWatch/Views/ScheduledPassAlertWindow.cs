using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OscarWatch.Localization;

namespace OscarWatch.Views;

/// <summary>Non-modal OK alert so CAT/rotator keep running during a scheduled-pass reminder.</summary>
public static class ScheduledPassAlertWindow
{
    public static void Show(Window? owner, string title, string message)
    {
        var l = LocalizationService.Instance;

        var okButton = new Button
        {
            Content = l.Get("Common.Ok"),
            MinWidth = 88,
            IsDefault = true,
            IsCancel = true
        };

        var window = new Window
        {
            Title = title,
            Width = 440,
            MinHeight = 140,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { okButton }
                    }
                }
            }
        };

        okButton.Click += (_, _) => window.Close();

        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}
