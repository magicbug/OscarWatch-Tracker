using Avalonia;
using Avalonia.Controls;

namespace OscarWatch.Views;

/// <summary>Shared apply / capture / clamp for floating windows that remember size and position.</summary>
internal static class DetachedWindowBounds
{
    public static void Apply(
        Window window,
        int? savedWidth,
        int? savedHeight,
        int? savedX,
        int? savedY,
        int minWidth,
        int minHeight)
    {
        if (savedWidth is int w && savedHeight is int h && w >= minWidth && h >= minHeight)
        {
            window.Width = w;
            window.Height = h;
        }

        if (savedX is not int x || savedY is not int y)
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = ClampToVisibleArea(window, new PixelPoint(x, y), minWidth, minHeight);
    }

    public static void Capture(
        Window window,
        out int width,
        out int height,
        out int x,
        out int y,
        int minWidth,
        int minHeight)
    {
        width = Math.Max(minWidth, (int)Math.Round(window.Width));
        height = Math.Max(minHeight, (int)Math.Round(window.Height));
        x = window.Position.X;
        y = window.Position.Y;
    }

    public static PixelPoint ClampToVisibleArea(
        Window window,
        PixelPoint position,
        int minWidth,
        int minHeight)
    {
        var screens = window.Screens;
        if (screens is null)
            return position;

        var screen = screens.ScreenFromPoint(position) ?? screens.Primary;
        if (screen is null)
            return position;

        var area = screen.WorkingArea;
        var width = (int)Math.Max(window.Width, minWidth);
        var height = (int)Math.Max(window.Height, minHeight);
        var maxX = Math.Max(area.X, area.Right - width);
        var maxY = Math.Max(area.Y, area.Bottom - height);
        var x = Math.Clamp(position.X, area.X, maxX);
        var y = Math.Clamp(position.Y, area.Y, maxY);
        return new PixelPoint(x, y);
    }
}
