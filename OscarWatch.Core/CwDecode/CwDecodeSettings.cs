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

    /// <summary>Hide one- and two-letter words the model is unsure of, which are almost always noise.</summary>
    public bool SquelchNoise { get; set; } = true;

    /// <summary>Keep the steady note nearest the decode band centred as it drifts.</summary>
    public bool FollowNote { get; set; } = true;

    /// <summary>Start listening when the focused satellite rises while the frequency panel is on CW.</summary>
    public bool AutoStartEnabled { get; set; }

    public double AutoStartElevationDeg { get; set; } = SstvSettings.DefaultAutoStartElevationDeg;

    public const double MinCopyFontSize = 10;
    public const double MaxCopyFontSize = 40;
    public const double DefaultCopyFontSize = 16;

    /// <summary>Text size of the decoded copy, in pixels.</summary>
    public double CopyFontSize { get; set; } = DefaultCopyFontSize;

    public static double ClampCopyFontSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinCopyFontSize, MaxCopyFontSize) : DefaultCopyFontSize;

    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    public void Normalise()
    {
        InputDeviceId ??= "";
        InputDeviceDisplayName ??= "";
        AutoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg);
        CopyFontSize = ClampCopyFontSize(CopyFontSize);
    }
}
