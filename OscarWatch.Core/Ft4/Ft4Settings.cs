using OscarWatch.Core.PskReporter;

namespace OscarWatch.Core.Ft4;

/// <summary>Persisted FT4 modem preferences.</summary>
public sealed class Ft4Settings
{
    public bool SkipRrr { get; set; } = true;

    public Ft4PttMethod PttMethod { get; set; } = Ft4PttMethod.Vox;

    public Ft4PttLine PttLine { get; set; } = Ft4PttLine.Rts;

    /// <summary>When true, the handshake line is inverted (active-low interfaces).</summary>
    public bool PttInvert { get; set; }

    /// <summary>COM port for <see cref="Ft4PttMethod.SeparateComPort"/>.</summary>
    public string SeparatePttPort { get; set; } = "";

    /// <summary>Milliseconds to assert PTT before audio starts.</summary>
    public int PttLeadMs { get; set; } = 200;

    /// <summary>Milliseconds to keep PTT after audio ends.</summary>
    public int PttTailMs { get; set; } = 100;

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

    /// <summary>When true, receive decodes are reported to PSK Reporter. Off by default.</summary>
    public bool PskReporterEnabled { get; set; }

    /// <summary>PSK Reporter UDP host. Not shown in the UI.</summary>
    public string PskReporterHost { get; set; } = PskReporterClient.DefaultHost;

    /// <summary>PSK Reporter UDP port. 14739 is the analyse-only test listener.</summary>
    public int PskReporterPort { get; set; } = PskReporterClient.DefaultPort;

    /// <summary>Font size for the decode / activity list (points). Default 12.</summary>
    public double DecodeFontSize { get; set; } = 12;

    /// <summary>Row background for a decode addressed to this station. #RRGGBB or #AARRGGBB.</summary>
    public string CallingMeColour { get; set; } = Ft4DecodeHighlight.DefaultCallingMeColour;

    /// <summary>Row background for the station in the current QSO. #RRGGBB or #AARRGGBB.</summary>
    public string ReplyingColour { get; set; } = Ft4DecodeHighlight.DefaultReplyingColour;

    /// <summary>Row background for a receive decode whose callsign is not in the logbook.</summary>
    public string NewCallColour { get; set; } = Ft4DecodeHighlight.DefaultNewCallColour;

    /// <summary>Row background when the callsign was worked but the 4-character grid is new.</summary>
    public string NewGridColour { get; set; } = Ft4DecodeHighlight.DefaultNewGridColour;

    /// <summary>Window size/position.</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    /// <summary>Per-satellite uplink calibration in kHz (shared across modes).</summary>
    public Dictionary<string, double> UplinkCalibrationKHzBySatellite { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public double GetUplinkCalibrationKHz(string? satelliteName)
    {
        if (string.IsNullOrWhiteSpace(satelliteName))
            return 0;
        return UplinkCalibrationKHzBySatellite.TryGetValue(satelliteName.Trim(), out var khz)
            ? khz
            : 0;
    }

    public void SetUplinkCalibrationKHz(string satelliteName, double khz)
    {
        var key = satelliteName.Trim();
        if (key.Length == 0)
            return;
        if (Math.Abs(khz) < 0.0001)
            UplinkCalibrationKHzBySatellite.Remove(key);
        else
            UplinkCalibrationKHzBySatellite[key] = Math.Clamp(khz, -20.0, 20.0);
    }
}
