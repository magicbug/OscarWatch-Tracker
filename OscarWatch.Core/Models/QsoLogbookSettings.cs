using OscarWatch.Core.Cw;

namespace OscarWatch.Core.Models;

public sealed class QsoLogbookSettings
{
    public CwKeyerSettings CwKeyer { get; set; } = new();

    /// <summary>
    /// Pixel widths for the Recent QSOs grid, keyed by <see cref="QsoLogbookHistoryColumns"/>.
    /// The comment column is omitted when it should fill remaining space (star width).
    /// </summary>
    public Dictionary<string, int> HistoryColumnWidthsPx { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int? WindowWidth { get; set; }

    public int? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }
}

public static class QsoLogbookWindowDefaults
{
    public const int DefaultWidth = 980;
    public const int DefaultHeight = 640;
    public const int MinWidth = 760;
    public const int MinHeight = 480;

    public static bool TryGetSavedSize(QsoLogbookSettings? settings, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (settings?.WindowWidth is not int w || settings.WindowHeight is not int h)
            return false;

        if (w < MinWidth || h < MinHeight)
            return false;

        width = w;
        height = h;
        return true;
    }

    public static bool TryGetSavedPosition(QsoLogbookSettings? settings, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (settings?.WindowX is not int savedX || settings.WindowY is not int savedY)
            return false;

        x = savedX;
        y = savedY;
        return true;
    }
}

public static class QsoLogbookHistoryColumns
{
    public const string DateTime = "dateTime";
    public const string Call = "call";
    public const string Country = "country";
    public const string Grid = "grid";
    public const string Satellite = "satellite";
    public const string Mode = "mode";
    public const string RstSent = "rstSent";
    public const string RstRcvd = "rstRcvd";
    public const string Cloudlog = "cloudlog";
    public const string Comment = "comment";

    public static IReadOnlyList<string> All { get; } =
    [
        DateTime,
        Call,
        Country,
        Grid,
        Satellite,
        Mode,
        RstSent,
        RstRcvd,
        Comment,
        Cloudlog
    ];

    public static bool UsesStarWidthByDefault(string key) =>
        string.Equals(key, Comment, StringComparison.OrdinalIgnoreCase);

    public static int DefaultPixelWidth(string key) => key switch
    {
        DateTime => 168,
        Call => 90,
        Country => 110,
        Grid => 70,
        Satellite => 80,
        Mode => 70,
        RstSent => 52,
        RstRcvd => 52,
        Cloudlog => 40,
        Comment => 0,
        _ => 80
    };

    public static int MinimumPixelWidth => 40;

    public static bool TryGetSavedPixelWidth(QsoLogbookSettings? settings, string key, out int pixels)
    {
        pixels = 0;
        if (settings is null)
            return false;

        if (!settings.HistoryColumnWidthsPx.TryGetValue(key, out pixels))
            return false;

        if (pixels < MinimumPixelWidth)
        {
            pixels = 0;
            return false;
        }

        return true;
    }
}
