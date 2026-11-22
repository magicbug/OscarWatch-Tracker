using Avalonia;
using Avalonia.Controls;
using OscarWatch.Core.Models;

namespace OscarWatch.Views;

/// <summary>Saves and restores the size and position of the Moon window, as the other tool windows do.</summary>
internal static class EmeWindowBounds
{
    public const int MinWidth = 460;
    public const int MinHeight = 420;

    public static void Apply(Window window, EmeSettings settings)
    {
        if (settings.WindowWidth is int w && settings.WindowHeight is int h && w >= MinWidth && h >= MinHeight)
        {
            window.Width = w;
            window.Height = h;
        }
        if (settings.WindowX is not int x || settings.WindowY is not int y)
            return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = QsoLogbookWindowBounds.ClampToVisibleArea(window, new PixelPoint(x, y));
    }

    public static void Capture(Window window, EmeSettings settings)
    {
        if (window.WindowState != WindowState.Normal)
            return;
        settings.WindowWidth = Math.Max(MinWidth, (int)Math.Round(window.Width));
        settings.WindowHeight = Math.Max(MinHeight, (int)Math.Round(window.Height));
        settings.WindowX = window.Position.X;
        settings.WindowY = window.Position.Y;
    }
}
