namespace OscarWatch.Core.Sstv;

/// <summary>
/// Receive modes, from the published SSTV mode specifications (JL Barber, "Proposal for
/// SSTV mode specifications", Dayton 2000). Scottie's sync sits before the red scan,
/// part way through the line, so its line starts with the separator before green.
/// </summary>
public static class SstvModeTable
{
    public static readonly SstvMode Robot36 = new(
        SstvModeId.Robot36, "Robot 36", 0x08, 320, 240, SstvColourSpace.YCbCr,
        [
            new(SstvChannel.Sync, 9.0),
            new(SstvChannel.Gap, 3.0),
            new(SstvChannel.Y0, 88.0),
            new(SstvChannel.Gap, 4.5),
            new(SstvChannel.Gap, 1.5),
            new(SstvChannel.AlternatingChroma, 44.0),
        ]);

    public static readonly SstvMode Robot72 = new(
        SstvModeId.Robot72, "Robot 72", 0x0C, 320, 240, SstvColourSpace.YCbCr,
        [
            new(SstvChannel.Sync, 9.0),
            new(SstvChannel.Gap, 3.0),
            new(SstvChannel.Y0, 138.0),
            new(SstvChannel.Gap, 4.5),
            new(SstvChannel.Gap, 1.5),
            new(SstvChannel.RMinusY, 69.0),
            new(SstvChannel.Gap, 4.5),
            new(SstvChannel.Gap, 1.5),
            new(SstvChannel.BMinusY, 69.0),
        ]);

    public static readonly SstvMode ScottieS1 = Scottie(SstvModeId.ScottieS1, "Scottie S1", 0x3C, 0.4320);
    public static readonly SstvMode ScottieS2 = Scottie(SstvModeId.ScottieS2, "Scottie S2", 0x38, 0.2752);
    public static readonly SstvMode ScottieDx = Scottie(SstvModeId.ScottieDx, "Scottie DX", 0x4C, 1.0800);

    public static readonly SstvMode MartinM1 = Martin(SstvModeId.MartinM1, "Martin M1", 0x2C, 0.4576);
    public static readonly SstvMode MartinM2 = Martin(SstvModeId.MartinM2, "Martin M2", 0x28, 0.2288);

    public static readonly SstvMode WraaseSc2180 = new(
        SstvModeId.WraaseSc2180, "Wraase SC2-180", 0x37, 320, 256, SstvColourSpace.Rgb,
        [
            new(SstvChannel.Sync, 5.5225),
            new(SstvChannel.Gap, 0.5),
            new(SstvChannel.Red, 235.0),
            new(SstvChannel.Green, 235.0),
            new(SstvChannel.Blue, 235.0),
        ]);

    public static readonly SstvMode Pd50 = Pd(SstvModeId.Pd50, "PD 50", 0x5D, 320, 256, 0.286);
    public static readonly SstvMode Pd90 = Pd(SstvModeId.Pd90, "PD 90", 0x63, 320, 256, 0.532);
    public static readonly SstvMode Pd120 = Pd(SstvModeId.Pd120, "PD 120", 0x5F, 640, 496, 0.190);
    public static readonly SstvMode Pd160 = Pd(SstvModeId.Pd160, "PD 160", 0x62, 512, 400, 0.382);
    public static readonly SstvMode Pd180 = Pd(SstvModeId.Pd180, "PD 180", 0x60, 640, 496, 0.286);
    public static readonly SstvMode Pd240 = Pd(SstvModeId.Pd240, "PD 240", 0x61, 640, 496, 0.382);
    public static readonly SstvMode Pd290 = Pd(SstvModeId.Pd290, "PD 290", 0x5E, 800, 616, 0.286);

    public static IReadOnlyList<SstvMode> All { get; } =
    [
        Robot36, Robot72,
        ScottieS1, ScottieS2, ScottieDx,
        MartinM1, MartinM2,
        WraaseSc2180,
        Pd50, Pd90, Pd120, Pd160, Pd180, Pd240, Pd290,
    ];

    public static SstvMode? FromVis(int visCode) =>
        All.FirstOrDefault(m => m.VisCode == visCode);

    public static SstvMode Get(SstvModeId id) => All.First(m => m.Id == id);

    private static SstvMode Scottie(SstvModeId id, string name, int vis, double pixelMs)
    {
        var scan = pixelMs * 320;
        return new SstvMode(
            id, name, vis, 320, 256, SstvColourSpace.Rgb,
            [
                new(SstvChannel.Gap, 1.5),
                new(SstvChannel.Green, scan),
                new(SstvChannel.Gap, 1.5),
                new(SstvChannel.Blue, scan),
                new(SstvChannel.Sync, 9.0),
                new(SstvChannel.Gap, 1.5),
                new(SstvChannel.Red, scan),
            ],
            leadInMs: 9.0);
    }

    private static SstvMode Martin(SstvModeId id, string name, int vis, double pixelMs)
    {
        var scan = pixelMs * 320;
        return new SstvMode(
            id, name, vis, 320, 256, SstvColourSpace.Rgb,
            [
                new(SstvChannel.Sync, 4.862),
                new(SstvChannel.Gap, 0.572),
                new(SstvChannel.Green, scan),
                new(SstvChannel.Gap, 0.572),
                new(SstvChannel.Blue, scan),
                new(SstvChannel.Gap, 0.572),
                new(SstvChannel.Red, scan),
                new(SstvChannel.Gap, 0.572),
            ]);
    }

    private static SstvMode Pd(SstvModeId id, string name, int vis, int width, int height, double pixelMs)
    {
        var scan = pixelMs * width;
        return new SstvMode(
            id, name, vis, width, height, SstvColourSpace.YCbCr,
            [
                new(SstvChannel.Sync, 20.0),
                new(SstvChannel.Gap, 2.08),
                new(SstvChannel.Y0, scan),
                new(SstvChannel.RMinusY, scan),
                new(SstvChannel.BMinusY, scan),
                new(SstvChannel.Y1, scan),
            ],
            rowsPerLine: 2);
    }
}
