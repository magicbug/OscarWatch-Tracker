namespace OscarWatch.Core.Sstv;

/// <summary>Persisted OscarWatch SSTV receive preferences.</summary>
public sealed class SstvSettings
{
    public const double DefaultAutoStartElevationDeg = -5;
    public const double MinAutoStartElevationDeg = -15;
    public const double MaxAutoStartElevationDeg = 30;

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

    /// <summary>
    /// Folder for pictures and session audio. Empty uses <see cref="SstvPaths.GetDefaultDirectory"/>.
    /// </summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>
    /// When true, finished pictures with a good lock are shared to the OscarWatch.org SSTV
    /// gallery after the satellite sets. Needs the OscarWatch API token. Default off.
    /// </summary>
    public bool SharePicturesWithOscarWatch { get; set; }

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

    /// <summary>
    /// Use the SSTV modes listed for the tracked satellite in the satellite database to reject
    /// pictures in other modes that are really noise. Default on.
    /// </summary>
    public bool UseSatelliteModes { get; set; } = true;

    /// <summary>Keep a picture going through a long fade once its line timing is known. Default on.</summary>
    public bool HoldThroughFades { get; set; } = true;

    /// <summary>Demodulate slower modes through a narrower filter to let less noise in. Default on.</summary>
    public bool AdaptiveBandwidth { get; set; } = true;

    /// <summary>
    /// Version of the fade defaults the saved settings were written with. Files saved before
    /// version 1 had <see cref="HoldThroughFades"/> and <see cref="AdaptiveBandwidth"/> off by default.
    /// </summary>
    public int FadeDefaultsVersion { get; set; } = CurrentFadeDefaultsVersion;

    public const int CurrentFadeDefaultsVersion = 1;

    /// <summary>Move a picture caught part way through at AOS down to the rows it belongs on. Default on.</summary>
    public bool AlignAosFragments { get; set; } = true;

    /// <summary>Redraw short runs of noisy lines from the lines either side. Default on.</summary>
    public bool RepairBadLines { get; set; } = true;

    /// <summary>Remove speckle from finished pictures at the cost of fine detail. Default off.</summary>
    public bool SmoothAcrossLines { get; set; }

    /// <summary>Merge repeat copies of a picture and save the merged picture as well. Default on.</summary>
    public bool CombineRepeats { get; set; } = true;

    /// <summary>
    /// When true, SSTV starts listening when the focused satellite rises above
    /// <see cref="AutoStartElevationDeg"/>, and stops when it sets. Default off.
    /// </summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// Elevation the focused satellite must reach before an automatic start.
    /// Negative values start before AOS so the radio and audio can settle. Default −5°.
    /// </summary>
    public double AutoStartElevationDeg { get; set; } = DefaultAutoStartElevationDeg;

    /// <summary>
    /// When true, the computer and display are kept out of idle sleep while SSTV is listening
    /// live. Decoding a saved recording does not. Default off.
    /// </summary>
    public bool KeepAwakeWhileListening { get; set; }

    /// <summary>Whether to hold the keep-awake request: only for a live session with the option on.</summary>
    public static bool ShouldKeepAwake(bool keepAwakeWhileListening, bool isRunning, bool isDecodingFile) =>
        keepAwakeWhileListening && isRunning && !isDecodingFile;

    public static double ClampAutoStartElevationDeg(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinAutoStartElevationDeg, MaxAutoStartElevationDeg)
            : DefaultAutoStartElevationDeg;

    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    public SstvModeId? GetForcedMode() =>
        Enum.TryParse<SstvModeId>(ForcedMode, ignoreCase: true, out var id) ? id : null;
}

/// <summary>The folder SSTV saves into, and the configured folder when it could not be used.</summary>
/// <param name="Path">Folder to save into.</param>
/// <param name="FellBack">True when <paramref name="Requested"/> was set but missing or not writable.</param>
/// <param name="Requested">The configured folder, or null when the setting was blank.</param>
public sealed record SstvOutputDirectory(string Path, bool FellBack, string? Requested);

/// <summary>Where SSTV pictures and session audio are kept.</summary>
public static class SstvPaths
{
    /// <summary>%AppData%\OscarWatch\sstv, next to the logs folder.</summary>
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "sstv");

    /// <summary>
    /// The folder to save into. A blank setting uses the default folder. A custom folder must
    /// already exist and accept a test file; otherwise the default is used and
    /// <see cref="SstvOutputDirectory.FellBack"/> is set.
    /// </summary>
    public static SstvOutputDirectory ResolveOutputDirectory(string? configured)
    {
        var defaultDirectory = GetDefaultDirectory();
        if (string.IsNullOrWhiteSpace(configured))
            return new SstvOutputDirectory(defaultDirectory, false, null);

        var requested = configured.Trim();
        return IsWritableDirectory(requested)
            ? new SstvOutputDirectory(requested, false, requested)
            : new SstvOutputDirectory(defaultDirectory, true, requested);
    }

    /// <summary>
    /// Operator-facing default path: <c>%APPDATA%\…</c> on Windows, <c>~/…</c> on Linux and macOS.
    /// </summary>
    public static string GetDefaultDirectoryDisplay()
    {
        var folder = GetDefaultDirectory();
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData) && folder.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
                return "%APPDATA%\\" + folder[appData.Length..].TrimStart('\\', '/').Replace('/', '\\');
            return folder;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && folder.StartsWith(home, StringComparison.Ordinal))
            return "~/" + folder[home.Length..].Replace('\\', '/').TrimStart('/');
        return folder.Replace('\\', '/');
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return false;

            var probe = Path.Combine(path, ".oscarwatch-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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
