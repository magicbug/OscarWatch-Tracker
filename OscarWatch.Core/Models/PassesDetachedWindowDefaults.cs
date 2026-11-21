namespace OscarWatch.Core.Models;

public static class PassesDetachedWindowDefaults
{
    public const int DefaultWidth = 420;
    public const int DefaultHeight = 640;
    public const int MinWidth = 320;
    public const int MinHeight = 280;

    public static bool TryGetSavedSize(AppSettings? settings, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (settings?.PassesDetachedWindowWidth is not int w || settings.PassesDetachedWindowHeight is not int h)
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
        if (settings?.PassesDetachedWindowX is not int savedX || settings.PassesDetachedWindowY is not int savedY)
            return false;

        x = savedX;
        y = savedY;
        return true;
    }
}
