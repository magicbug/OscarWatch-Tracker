using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.PskReporter;

namespace OscarWatch.Core.Ft4;

/// <summary>Persisted FT4 modem preferences.</summary>
public sealed class Ft4Settings
{
    public bool SkipRrr { get; set; } = true;

    /// <summary>
    /// When true (default), a report, RR73, or 73 that the first decode missed is tried again
    /// once both calls are known. Those lines are marked AP.
    /// </summary>
    public bool ApEnabled { get; set; } = true;

    public PttMethod PttMethod { get; set; } = PttMethod.Vox;

    public PttLine PttLine { get; set; } = PttLine.Rts;

    /// <summary>When true, the handshake line is inverted (active-low interfaces).</summary>
    public bool PttInvert { get; set; }

    /// <summary>COM port for <see cref="PttMethod.SeparateComPort"/>.</summary>
    public string SeparatePttPort { get; set; } = "";

    /// <summary>Milliseconds to assert PTT before audio starts.</summary>
    public int PttLeadMs { get; set; } = 200;

    /// <summary>Milliseconds to keep PTT after audio ends.</summary>
    public int PttTailMs { get; set; } = 100;

    public PttOptions ToPttOptions() =>
        new(PttMethod, PttLine, PttInvert, SeparatePttPort ?? "", PttLeadMs, PttTailMs);

    /// <summary>Durable PortAudio device name for capture (not a volatile enumeration index). Empty = system default.</summary>
    public string InputDeviceId { get; set; } = "";

    public string InputDeviceDisplayName { get; set; } = "";

    /// <summary>Durable PortAudio device name for playback (not a volatile enumeration index). Empty = system default.</summary>
    public string OutputDeviceId { get; set; } = "";

    public string OutputDeviceDisplayName { get; set; } = "";

    /// <summary>
    /// Clears legacy numeric PortAudio indices so rematch uses the stored display names
    /// (same pattern as pass recording).
    /// </summary>
    public void MigrateLegacyNumericDeviceIds()
    {
        if (IsLegacyNumericId(InputDeviceId))
            InputDeviceId = "";
        if (IsLegacyNumericId(OutputDeviceId))
            OutputDeviceId = "";
    }

    private static bool IsLegacyNumericId(string? deviceId)
    {
        var id = deviceId?.Trim() ?? "";
        if (id.Length == 0)
            return false;
        for (var i = 0; i < id.Length; i++)
        {
            if (!char.IsAsciiDigit(id[i]))
                return false;
        }

        return true;
    }

    /// <summary>Transmit audio level 0–1.</summary>
    public double TxLevel { get; set; } = 0.35;

    /// <summary>Default TX audio frequency in Hz within the USB passband.</summary>
    public double TxAudioHz { get; set; } = 1500;

    /// <summary>
    /// When true (default), answering a decode keeps the current TX audio Hz
    /// (WSJT-X “Hold Tx Freq”). When false, TX audio jumps to the decode frequency.
    /// </summary>
    public bool HoldTxFrequency { get; set; } = true;

    /// <summary>
    /// When true (default), the first station that calls while CQ is running is answered
    /// automatically. When false, CQ continues until the operator clicks a station.
    /// A callsign in <see cref="BlockedCallsigns"/> is left unanswered either way until clicked.
    /// </summary>
    public bool AutoReply { get; set; } = true;

    /// <summary>
    /// Callsigns that are not answered automatically while calling CQ.
    /// Clicking their decode still starts a contact. A trailing /P or /R matches the base callsign.
    /// Replace the list when it changes. The sequencer reads it on the decode thread.
    /// </summary>
    public List<string> BlockedCallsigns { get; set; } = [];

    /// <summary>
    /// When true, FT4 sets the radio's RF power to <see cref="Ft4RfPowerLimit.MaxWatts"/>
    /// before transmitting if the radio reports a higher setting. ICOM uses CI-V.
    /// Yaesu FT-991/FT-991A and Kenwood TS-2000 use the <c>PC</c> command. The radio stays at that level.
    /// </summary>
    public bool AutoLowerRfPower { get; set; }

    /// <summary>
    /// When true (default), hold CAT Doppler for each FT4 slot and cancel within-slot
    /// uplink drift in the TX audio (OrbitDeck audioDopplerTX).
    /// </summary>
    public bool AudioDopplerTx { get; set; } = true;

    /// <summary>
    /// When true (default), de-Doppler each RX slot in audio before decode
    /// (OrbitDeck audioDopplerRX).
    /// </summary>
    public bool AudioDopplerRx { get; set; } = true;

    /// <summary>
    /// When true (default), on TX slots run the normal decode and a late-echo pass
    /// together so a full-duplex own copy appears sooner (uses more CPU for that slot).
    /// </summary>
    public bool ParallelTxEchoDecode { get; set; } = true;

    /// <summary>
    /// When true, each receive slot's audio and decode passes are saved for decoder testing
    /// (about 180 kB a slot, kept for <see cref="Ft4SlotRecordingFiles.RetainedDays"/> days). Off by default.
    /// </summary>
    public bool SaveSlotAudio { get; set; }

    /// <summary>
    /// Minutes of transmit with no reply before TX is halted. 0 disables the watchdog.
    /// </summary>
    public int TxWatchdogMinutes { get; set; } = Ft4TxWatchdog.DefaultMinutes;

    /// <summary>
    /// When true (default), the DT column shows the WSJT-X convention: the 0.5 s FT4 slot
    /// start is subtracted so an on-time signal reads near 0, matching WSJT-X. When false,
    /// DT is shown relative to the slot start (an on-time signal reads about +0.5 s).
    /// Display only; decode filtering always uses the raw slot-relative DT.
    /// </summary>
    public bool ShowDtWsjtxConvention { get; set; } = true;

    /// <summary>When true, receive decodes are reported to PSK Reporter. Off by default.</summary>
    public bool PskReporterEnabled { get; set; }

    /// <summary>
    /// When true, receive decodes are posted to OscarWatch.org satellite spots.
    /// Off by default. Unused unless Settings, OscarWatch has an API token.
    /// </summary>
    public bool OscarWatchSpotsEnabled { get; set; }

    /// <summary>PSK Reporter UDP host. Not shown in the UI.</summary>
    public string PskReporterHost { get; set; } = PskReporterClient.DefaultHost;

    /// <summary>PSK Reporter UDP port. 14739 is the analyse-only test listener.</summary>
    public int PskReporterPort { get; set; } = PskReporterClient.DefaultPort;

    /// <summary>Font size for the decode / activity list (points). Default 12.</summary>
    public double DecodeFontSize { get; set; } = 12;

    /// <summary>Basic is one decode list. Advanced adds a column for calls to this station.</summary>
    public Ft4DecodeLayout DecodeLayout { get; set; } = Ft4DecodeLayout.Basic;

    /// <summary>dB span above the automatic waterfall noise floor. Default 40.</summary>
    public const double DefaultWaterfallRangeDb = 40;

    public const double MinWaterfallRangeDb = 15;

    public const double MaxWaterfallRangeDb = 80;

    /// <summary>
    /// How many decibels above the automatic noise floor the waterfall paints.
    /// A smaller span makes signals stand out more. The floor itself still tracks.
    /// </summary>
    public double WaterfallRangeDb { get; set; } = DefaultWaterfallRangeDb;

    public static double ClampWaterfallRangeDb(double value) =>
        Math.Clamp(value, MinWaterfallRangeDb, MaxWaterfallRangeDb);

    /// <summary>
    /// Even out the slope of the radio's SSB filter so the passband edges are not darker
    /// than the middle (like WSJT-X Flatten). On by default.
    /// </summary>
    public bool WaterfallFlatten { get; set; } = true;

    /// <summary>Draw a line at the start of each slot with its UTC start time (like WSJT-X). On by default.</summary>
    public bool WaterfallSlotLines { get; set; } = true;

    /// <summary>Slowest waterfall scroll. About 48 seconds stay on screen.</summary>
    public const int SlowWaterfallSpeed = 1;

    /// <summary>Middle waterfall scroll. About 24 seconds stay on screen.</summary>
    public const int MediumWaterfallSpeed = 2;

    /// <summary>Original waterfall scroll. About 12 seconds stay on screen.</summary>
    public const int FastWaterfallSpeed = 3;

    public const int DefaultWaterfallSpeed = FastWaterfallSpeed;

    public const int MinWaterfallSpeed = SlowWaterfallSpeed;

    public const int MaxWaterfallSpeed = FastWaterfallSpeed;

    /// <summary>
    /// FT4 window refresh, in milliseconds. Waterfall speed counts these ticks.
    /// The slot clock uses the same timer.
    /// </summary>
    public const int WaterfallUiTickMs = 100;

    /// <summary>
    /// How fast the waterfall scrolls. <see cref="SlowWaterfallSpeed"/> draws a new
    /// line every four refreshes, <see cref="MediumWaterfallSpeed"/> every two,
    /// and <see cref="FastWaterfallSpeed"/> (the default) every refresh.
    /// Slower speeds keep more of the pass on screen and build the spectrum less often.
    /// </summary>
    public int WaterfallSpeed { get; set; } = DefaultWaterfallSpeed;

    public static int ClampWaterfallSpeed(int value) =>
        Math.Clamp(value, MinWaterfallSpeed, MaxWaterfallSpeed);

    /// <summary>How many <see cref="WaterfallUiTickMs"/> refreshes pass before a new waterfall line.</summary>
    public static int WaterfallTicksPerRow(int speed) =>
        ClampWaterfallSpeed(speed) switch
        {
            SlowWaterfallSpeed => 4,
            MediumWaterfallSpeed => 2,
            _ => 1,
        };

    /// <summary>
    /// Approximate seconds kept on screen for <paramref name="historyRows"/> at this speed.
    /// The row count stays fixed, so a slower speed covers more time.
    /// </summary>
    public static int WaterfallHistorySeconds(int speed, int historyRows) =>
        historyRows < 1 ? 0 : historyRows * WaterfallTicksPerRow(speed) * WaterfallUiTickMs / 1000;

    /// <summary>Row background for a decode addressed to this station. #RRGGBB or #AARRGGBB.</summary>
    public string CallingMeColour { get; set; } = Ft4DecodeHighlight.DefaultCallingMeColour;

    /// <summary>Row background for the station in the current QSO. #RRGGBB or #AARRGGBB.</summary>
    public string ReplyingColour { get; set; } = Ft4DecodeHighlight.DefaultReplyingColour;

    /// <summary>Row background for a receive decode whose callsign is not in the logbook.</summary>
    public string NewCallColour { get; set; } = Ft4DecodeHighlight.DefaultNewCallColour;

    /// <summary>Row background when the callsign was worked but the 4-character grid is new.</summary>
    public string NewGridColour { get; set; } = Ft4DecodeHighlight.DefaultNewGridColour;

    /// <summary>Row background for a received CQ. #RRGGBB or #AARRGGBB.</summary>
    public string CqColour { get; set; } = Ft4DecodeHighlight.DefaultCqColour;

    /// <summary>Text colour for a decode addressed to this station. Empty follows the theme.</summary>
    public string CallingMeTextColour { get; set; } = "";

    /// <summary>Text colour for the station in the current QSO. Empty follows the theme.</summary>
    public string ReplyingTextColour { get; set; } = "";

    /// <summary>Text colour for a receive decode whose callsign is not in the logbook. Empty follows the theme.</summary>
    public string NewCallTextColour { get; set; } = "";

    /// <summary>Text colour when the callsign was worked but the 4-character grid is new. Empty follows the theme.</summary>
    public string NewGridTextColour { get; set; } = "";

    /// <summary>Text colour for a received CQ. Empty follows the theme.</summary>
    public string CqTextColour { get; set; } = "";

    /// <summary>Text colour for a message this station transmitted.</summary>
    public string TxTextColour { get; set; } = Ft4DecodeHighlight.DefaultTxTextColour;

    /// <summary>Window size/position. Size and position are the last normal (not maximised) bounds.</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximised { get; set; }

    public const double DefaultDecodeColumnShare = 0.5;

    public const double MinDecodeColumnShare = 0.25;

    public const double MaxDecodeColumnShare = 0.75;

    /// <summary>Share of the decode area given to Band activity in the advanced layout. The rest is Calling me.</summary>
    public double DecodeColumnShare { get; set; } = DefaultDecodeColumnShare;

    public static double ClampDecodeColumnShare(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinDecodeColumnShare, MaxDecodeColumnShare)
            : DefaultDecodeColumnShare;

    /// <summary>
    /// Uplink echo calibration in kHz. Keys are either a legacy satellite name (RS-44)
    /// or a <see cref="Ft4UplinkCalibration.FormatTransponderKey"/> fingerprint.
    /// A legacy value is the fallback for FT4 transponders that do not yet have their own trim.
    /// </summary>
    public Dictionary<string, double> UplinkCalibrationKHzBySatellite { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public double GetUplinkCalibrationKHz(string? satelliteName, SatelliteTransponderMode? mode = null)
    {
        if (mode is not null && Ft4SlotGate.IsFt4Transponder(mode)
            && !string.IsNullOrWhiteSpace(satelliteName))
        {
            var transponderKey = Ft4UplinkCalibration.FormatTransponderKey(satelliteName, mode);
            if (transponderKey.Length > 0
                && UplinkCalibrationKHzBySatellite.TryGetValue(transponderKey, out var perMode))
                return perMode;
        }

        if (string.IsNullOrWhiteSpace(satelliteName))
            return 0;

        return UplinkCalibrationKHzBySatellite.TryGetValue(satelliteName.Trim(), out var legacy)
            ? legacy
            : 0;
    }

    public void SetUplinkCalibrationKHz(string? satelliteName, double khz, SatelliteTransponderMode? mode = null)
    {
        string key;
        var perTransponder = mode is not null && Ft4SlotGate.IsFt4Transponder(mode)
            && !string.IsNullOrWhiteSpace(satelliteName);
        if (perTransponder)
        {
            key = Ft4UplinkCalibration.FormatTransponderKey(satelliteName, mode);
            if (key.Length == 0)
                return;
        }
        else if (!string.IsNullOrWhiteSpace(satelliteName))
        {
            key = satelliteName.Trim();
        }
        else
        {
            return;
        }

        if (Math.Abs(khz) < 0.0001)
        {
            // A stored zero on one FT4 row must not fall back to the satellite-wide trim.
            if (perTransponder)
                UplinkCalibrationKHzBySatellite[key] = 0;
            else
                UplinkCalibrationKHzBySatellite.Remove(key);
            return;
        }

        UplinkCalibrationKHzBySatellite[key] = Math.Clamp(khz, -20.0, 20.0);
    }
}
