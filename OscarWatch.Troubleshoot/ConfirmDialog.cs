using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

/// <summary>Continue or Cancel. Cancel has focus and Escape cancels, because every confirm here changes something.</summary>
internal static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string message)
    {
        var dialog = new Window
        {
            Title = Tr.Get("Window.Title"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var cancel = new Button { Content = Tr.Get("Confirm.Cancel"), IsCancel = true, MinWidth = 90 };
        var proceed = new Button { Content = Tr.Get("Confirm.Continue"), MinWidth = 90 };
        cancel.Click += (_, _) => dialog.Close(false);
        proceed.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { proceed, cancel }
                }
            }
        };

        dialog.Opened += (_, _) => cancel.Focus(NavigationMethod.Tab);
        return await dialog.ShowDialog<bool>(owner);
    }
}
