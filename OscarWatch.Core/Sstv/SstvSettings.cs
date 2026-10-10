namespace OscarWatch.Core.Sstv;

/// <summary>Persisted OscarWatch SSTV receive preferences.</summary>
public sealed class SstvSettings
{
    /// <summary>Durable PortAudio device name for capture. Empty = system default.</summary>
    public string InputDeviceId { get; set; } = "";

    public string InputDeviceDisplayName { get; set; } = "";

    /// <summary>When true (default), every finished or cut-short picture is saved as PNG.</summary>
    public bool AutoSavePictures { get; set; } = true;

    /// <summary>
    /// When true (default), the receive audio is kept as a 12 kHz WAV next to the pictures
    /// (about 1.4 MB a minute) so a pass can be decoded again later.
    /// </summary>
    public bool SaveSessionAudio { get; set; } = true;

    /// <summary>Mode to decode as regardless of the VIS. Empty picks the mode automatically.</summary>
    public string ForcedMode { get; set; } = "";

    /// <summary>Measure the line period from the sync pulses (slant correction). Default on.</summary>
    public bool AutoSlant { get; set; } = true;

    /// <summary>Manual clock trim in ppm, used when <see cref="AutoSlant"/> is off.</summary>
    public double SlantTrimPpm { get; set; }

    /// <summary>Follow the sync tone frequency line by line. Default on.</summary>
    public bool AutoTune { get; set; } = true;

    /// <summary>
    /// When true (default), the audio offset left by rig-control Doppler steps (SSB only)
    /// is removed before decoding.
    /// </summary>
    public bool CatDopplerFeedForward { get; set; } = true;

    /// <summary>Look for a picture from its sync pulses when the VIS header was lost. Default on.</summary>
    public bool DetectWithoutVis { get; set; } = true;

    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    public SstvModeId? GetForcedMode() =>
        Enum.TryParse<SstvModeId>(ForcedMode, ignoreCase: true, out var id) ? id : null;
}

/// <summary>Where SSTV pictures and session audio are kept.</summary>
public static class SstvPaths
{
    /// <summary>%AppData%\OscarWatch\sstv, next to the logs folder.</summary>
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "sstv");

    /// <summary>File name stem for a picture: UTC time, mode and an optional satellite.</summary>
    public static string PictureStem(DateTime utc, SstvMode mode, string? satellite)
    {
        var sat = string.IsNullOrWhiteSpace(satellite) ? "" : "_" + Sanitise(satellite);
        return $"{utc:yyyyMMdd_HHmmss}Z{sat}_{mode.Id}";
    }

    public static string SessionAudioStem(DateTime utc, string? satellite)
    {
        var sat = string.IsNullOrWhiteSpace(satellite) ? "" : "_" + Sanitise(satellite);
        return $"{utc:yyyyMMdd_HHmmss}Z{sat}_session";
    }

    private static string Sanitise(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray();
        return new string(chars);
    }
}
