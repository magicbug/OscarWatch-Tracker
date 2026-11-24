using OscarWatch.Core.Sstv;

namespace OscarWatch.Core.CwDecode;

/// <summary>Persisted OscarWatch CW decoder preferences.</summary>
public sealed class CwDecodeSettings
{
    /// <summary>Durable PortAudio device name for capture. Empty = system default.</summary>
    public string InputDeviceId { get; set; } = "";

    public string InputDeviceDisplayName { get; set; } = "";

    /// <summary>
    /// When true (default), the note offset left by rig-control Doppler steps (SSB downlinks)
    /// is moved back before decoding.
    /// </summary>
    public bool CatDopplerFeedForward { get; set; } = true;

    /// <summary>
    /// Legacy on/off for older settings files. Prefer <see cref="SquelchMode"/>.
    /// Kept so a saved false still maps to Off when SquelchMode is missing.
    /// </summary>
    public bool SquelchNoise { get; set; } = true;

    /// <summary>How aggressively short unsure letters are hidden. Null means use <see cref="SquelchNoise"/>.</summary>
    public CwNoiseSquelchMode? SquelchMode { get; set; }

    /// <summary>Resolved noise filter after bridging the legacy checkbox.</summary>
    public CwNoiseSquelchMode EffectiveSquelchMode =>
        SquelchMode ?? (SquelchNoise ? CwNoiseSquelchMode.Normal : CwNoiseSquelchMode.Off);

    /// <summary>Keep the steady note nearest the decode band centred as it drifts.</summary>
    public bool FollowNote { get; set; } = true;

    /// <summary>Start listening when the focused satellite rises while the frequency panel is on CW.</summary>
    public bool AutoStartEnabled { get; set; }

    public double AutoStartElevationDeg { get; set; } = SstvSettings.DefaultAutoStartElevationDeg;

    public const double MinCopyFontSize = 10;
    public const double MaxCopyFontSize = 40;
    public const double DefaultCopyFontSize = 16;

    public const int MinWaterfallHeight = 80;
    public const int MaxWaterfallHeight = 300;
    public const int DefaultWaterfallHeight = 120;

    /// <summary>Text size of the decoded copy, in pixels.</summary>
    public double CopyFontSize { get; set; } = DefaultCopyFontSize;

    public static double ClampCopyFontSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinCopyFontSize, MaxCopyFontSize) : DefaultCopyFontSize;

    /// <summary>Height of the waterfall panel in the CW window, in pixels.</summary>
    public int? WaterfallHeight { get; set; }

    public static int ClampWaterfallHeight(double height) =>
        double.IsFinite(height)
            ? (int)Math.Clamp(Math.Round(height), MinWaterfallHeight, MaxWaterfallHeight)
            : DefaultWaterfallHeight;

    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    public void Normalise()
    {
        InputDeviceId ??= "";
        InputDeviceDisplayName ??= "";
        AutoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg);
        CopyFontSize = ClampCopyFontSize(CopyFontSize);
        if (WaterfallHeight is { } height)
            WaterfallHeight = ClampWaterfallHeight(height);

        var mode = EffectiveSquelchMode;
        if (!Enum.IsDefined(mode))
            mode = CwNoiseSquelchMode.Normal;
        SquelchMode = mode;
        SquelchNoise = mode != CwNoiseSquelchMode.Off;
    }
}
