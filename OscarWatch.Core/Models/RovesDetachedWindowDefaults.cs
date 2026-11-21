namespace OscarWatch.Core.Models;

public static class RovesDetachedWindowDefaults
{
    public const int DefaultWidth = 420;
    public const int DefaultHeight = 360;
    public const int MinWidth = 320;
    public const int MinHeight = 200;

    public static bool TryGetSavedSize(AppSettings? settings, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (settings?.RovesDetachedWindowWidth is not int w || settings.RovesDetachedWindowHeight is not int h)
            return false;

        if (w < MinWidth || h < MinHeight)
            return false;

        width = w;
        height = h;
        return true;
    }

    public static bool TryGetSavedPosition(AppSettings? settings, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (settings?.RovesDetachedWindowX is not int savedX || settings.RovesDetachedWindowY is not int savedY)
            return false;

        x = savedX;
        y = savedY;
        return true;
    }
}
