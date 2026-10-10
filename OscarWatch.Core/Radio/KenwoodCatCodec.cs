namespace OscarWatch.Core.Radio;

/// <summary>
/// Kenwood TS-2000 ASCII CAT command encoding (Hamlib-compatible subset).
/// Frequencies are 11-digit Hz; CTCSS indices are 1-based per ts2000_ctcss_list.
/// </summary>
public static class KenwoodCatCodec
{
    public const int FrequencyDigits = 11;
    public const int ToneTableBase = 1;

    /// <summary>TS-2000 CTCSS list (Hz), Hamlib ts2000_ctcss_list — excludes 17500 tone.</summary>
    public static readonly int[] CtcssTonesHz =
    [
        670, 719, 744, 770, 797, 825, 854, 885, 915, 948,
        974, 1000, 1035, 1072, 1109, 1148, 1188, 1230, 1273, 1318,
        1365, 1413, 1462, 1514, 1567, 1622, 1679, 1738, 1799, 1862,
        1928, 2035, 2107, 2181, 2257, 2336, 2418, 2503
    ];

    public static string BuildSetFrequencyCommand(char vfoLetter, long hz)
    {
        var letter = char.ToUpperInvariant(vfoLetter);
        if (letter is not ('A' or 'B' or 'C'))
            throw new ArgumentOutOfRangeException(nameof(vfoLetter));

        if (hz < 0)
            throw new ArgumentOutOfRangeException(nameof(hz));

        return $"F{letter}{hz:D11};";
    }

    public static string BuildReadFrequencyCommand(char vfoLetter) =>
        $"F{char.ToUpperInvariant(vfoLetter)};";

    public static string BuildSetModeCommand(char modeCode) => $"MD{modeCode};";

    public static string BuildSatelliteStatusQuery() => "SA;";

    /// <summary>
    /// Enter SATL: P1 on, mem 0, Main=downlink/Sub=uplink, CTRL main, VFO mode.
    /// When <paramref name="traceEnabled"/> is true, TRACE and TRACE REV are on (<c>SA1010110;</c>);
    /// when false, both are off (<c>SA1010000;</c>) for PC-managed Doppler.
    /// </summary>
    public static string BuildSetSatelliteModeOnCommand(bool traceEnabled = true) =>
        traceEnabled ? "SA1010110;" : "SA1010000;";

    /// <summary>Encode-tone off commands sent when entering SATL (pre-tracking).</summary>
    public static readonly string[] SatelliteModeEntryToneOffSequence = ["TO0;", "TO0;"];

    /// <summary>Extended Auto Information on during SAT entry (reduces explicit reads during tracking).</summary>
    public static string BuildAutoinfoExtendedCommand() => "AI2;";

    /// <summary>SATL entry: sent after initial <c>FA;</c> read (TS-2000 satellite CAT handshake).</summary>
    public static string BuildSatelliteEntryTsCommand() => "TS1;";

    /// <summary>Minimum interval between FA; link-hold polls while SATL tracking (SatPC32 ~1/s).</summary>
    public const int SatelliteLinkHoldPollIntervalMs = 1000;

    /// <summary>Read timeout for FA;/FB; (link hold waits for response).</summary>
    public const int FrequencyReadTimeoutMs = 450;

    public static int GetReplyTimeoutMs(string command, int postDelayMs)
    {
        var body = command.Trim().TrimEnd(';');
        if (body.Length == 2 && body[0] is 'F' or 'f')
            return FrequencyReadTimeoutMs;

        return body.ToUpperInvariant() switch
        {
            "SA" or "RX" or "FR" or "ID" => Math.Max(postDelayMs + 400, 600),
            _ => Math.Max(postDelayMs + 200, 400)
        };
    }

    /// <summary>TS-2000 <c>ID;</c> reply is <c>ID019;</c>. 020 is the TS-2000X variant.</summary>
    public static bool TryParseIdentification(ReadOnlySpan<char> response, out string id) =>
        TryParseFixedIdentification(response, digitCount: 3, out id);

    public static bool TryParseFixedIdentification(ReadOnlySpan<char> response, int digitCount, out string id)
    {
        id = "";
        var text = response.Trim();
        if (text.Length < 2 + digitCount)
            return false;
        if (text.Length < 2 || (text[0] != 'I' && text[0] != 'i') || (text[1] != 'D' && text[1] != 'd'))
            return false;

        var digits = text[2..].TrimEnd(';').Trim();
        if (digits.Length != digitCount)
            return false;

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
                return false;
        }

        id = digits.ToString();
        return true;
    }

    public static bool IsReadCommand(string command)
    {
        var body = command.Trim().TrimEnd(';');
        if (body.Length == 2 && body[0] is 'F' or 'f')
            return body[1] is 'A' or 'a' or 'B' or 'b' or 'C' or 'c';

        return body.Equals("SA", StringComparison.OrdinalIgnoreCase)
            || body.Equals("RX", StringComparison.OrdinalIgnoreCase)
            || body.Equals("FR", StringComparison.OrdinalIgnoreCase)
            || body.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || body.Equals("PC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SATL on with CTRL on sub (before uplink <c>MD</c> / tone on Sub).
    /// TRACE/TRACE REV follow <paramref name="traceEnabled"/> (<c>SA1011110;</c> vs <c>SA1011000;</c>).
    /// </summary>
    public static string BuildSetSatelliteModeOnSubControlCommand(bool traceEnabled = true) =>
        traceEnabled ? "SA1011110;" : "SA1011000;";

    /// <summary>Band select after <c>FA</c>/<c>FB</c> in SATL (main then sub band index).</summary>
    public static string BuildSatelliteBandSelectMainCommand() => "SM10000;";

    public static string BuildSatelliteBandSelectSubCommand(long hz) =>
        hz >= 200_000_000 ? "SM00004;" : "SM00021;";

    /// <summary>Short SAT off (manual P1=0 only).</summary>
    public static string BuildSetSatelliteModeOffCommand() => "SA0;";

    /// <summary>
    /// SATL exit: read RX status, clear encode tone, then SAT off.
    /// (No TN table reset — those SatPC32 TN39 commands cause rejection beeps on many radios.)
    /// </summary>
    public static readonly string[] SatelliteModeExitSequence =
    [
        "RX;",
        "TO0;",
        "SA0010000;"
    ];

    public static bool IsSatelliteModeExitReadCommand(string command) =>
        string.Equals(command, "RX;", StringComparison.OrdinalIgnoreCase);

    public static string BuildAutoinfoOffCommand() => "AI0;";

    public static string BuildSelectVfoCommand(bool vfoB) => vfoB ? "FR1;" : "FR0;";

    public static string BuildReadVfoSelectCommand() => "FR;";

    public static string BuildSetVfoSelectCommand(char selectCode) => $"FR{selectCode};";

    /// <summary>FR/FT memory channel select (blocks SAT entry until cleared).</summary>
    public const char VfoSelectMemoryCode = '2';

    /// <summary>DC P1=1 P2=1 — TX and CTRL on sub (query/program sub receiver).</summary>
    public static string BuildControlSubReceiverCommand() => "DC11;";

    /// <summary>
    /// DC P1=1 P2=0 — TX (PTT) on sub, CTRL on main. Best-effort after SATL entry so uplink is on SUB.
    /// </summary>
    public static string BuildTxSubControlMainCommand() => "DC10;";

    public static bool TryParseVfoSelect(ReadOnlySpan<char> response, out char selectCode)
    {
        selectCode = default;
        for (var i = 0; i < response.Length - 2; i++)
        {
            if (response[i] is not ('F' or 'f') || response[i + 1] is not ('R' or 'r'))
                continue;

            selectCode = response[i + 2];
            return selectCode is >= '0' and <= '9';
        }

        return false;
    }

    /// <summary>CTCSS squelch (TSQL) tone frequency — Hamlib set_ctcss_sql.</summary>
    public static string BuildCtcssFrequencyCommand(int oneBasedIndex) =>
        $"CN{oneBasedIndex:D2};";

    /// <summary>CTCSS encode tone frequency — Hamlib set_ctcss_tone (TN).</summary>
    public static string BuildToneFrequencyCommand(int oneBasedIndex) =>
        $"TN{oneBasedIndex:D2};";

    /// <summary>CTCSS squelch (TSQL) on/off — Hamlib RIG_FUNC_TSQL.</summary>
    public static string BuildCtcssEnableCommand(bool on) => on ? "CT1;" : "CT0;";

    /// <summary>CTCSS encode on/off — Hamlib RIG_FUNC_TONE.</summary>
    public static string BuildToneEnableCommand(bool on) => on ? "TO1;" : "TO0;";

    /// <summary>DC P1=0 P2=0 — TX and CTRL on main (VFO A / downlink in SATL).</summary>
    public static string BuildControlMainCommand() => "DC00;";

    /// <summary>DC P1=0 P2=1 — TX main, CTRL sub (tone/CTCSS and <c>MD</c> on sub band).</summary>
    public static string BuildControlSubCommand() => "DC01;";

    /// <summary>
    /// Read set RF power (watts) for the current transmit band.
    /// Answer is <c>PCnnn;</c> with nnn = 005–100.
    /// </summary>
    public static string BuildReadPowerCommand() => "PC;";

    /// <summary>Set RF power. <c>PCnnn;</c> with nnn = 005–100.</summary>
    public static bool TryBuildSetPowerCommand(double watts, out string command)
    {
        command = "";
        var rounded = (int)Math.Round(watts);
        if (rounded is < 5 or > 100)
            return false;

        command = $"PC{rounded:D3};";
        return true;
    }

    /// <summary>Parse <c>PCnnn;</c> (or bare digits) into watts. Manual range is 005–100.</summary>
    public static bool TryParsePowerWatts(ReadOnlySpan<char> response, out int watts)
    {
        watts = 0;
        if (response.IsEmpty)
            return false;

        var span = response.Trim();
        if (span.EndsWith(";"))
            span = span[..^1];

        if (span.Length >= 2
            && (span[0] is 'P' or 'p')
            && (span[1] is 'C' or 'c'))
            span = span[2..];

        span = span.Trim();
        if (span.IsEmpty)
            return false;

        if (!int.TryParse(span, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        // Manual: 005–100. Accept a slightly wider range for firmware quirks.
        if (value is < 1 or > 100)
            return false;

        watts = value;
        return true;
    }

    public static bool TryParseFrequencyHz(ReadOnlySpan<char> response, out long hz)
    {
        hz = 0;
        if (response.Length < 2 + FrequencyDigits)
            return false;

        var start = response[0] is 'F' or 'f' ? 2 : 0;
        if (start + FrequencyDigits > response.Length)
            return false;

        long value = 0;
        for (var i = 0; i < FrequencyDigits; i++)
        {
            var c = response[start + i];
            if (c is < '0' or > '9')
                return false;
            value = value * 10 + (c - '0');
        }

        hz = value;
        return true;
    }

    public static bool TryParseSatelliteOn(ReadOnlySpan<char> response)
    {
        // Hamlib: SA response, satellite on when retbuf[2] == '1'
        for (var i = 0; i < response.Length - 2; i++)
        {
            if ((response[i] is 'S' or 's') && (response[i + 1] is 'A' or 'a'))
                return response[i + 2] == '1';
        }

        return false;
    }

    public static bool TryGetModeCode(string mode, out char modeCode)
    {
        modeCode = default;
        var upper = mode.ToUpperInvariant();
        var code = upper switch
        {
            "LSB" or "DATA-LSB" => '1',
            "USB" or "DATA-USB" => '2',
            "CW" => '3',
            "FM" or "FMN" or "DATA-FM" or "FM-DATA" => '4',
            "AM" => '5',
            _ => (char)0
        };

        if (code == 0)
            return false;

        modeCode = code;
        return true;
    }

    public static bool TryGetCtcssIndex(double toneHz, out int oneBasedIndex)
    {
        oneBasedIndex = 0;
        // Hamlib tone_t values are tenths of a Hz (670 => 67.0 Hz).
        var target = (int)Math.Round(toneHz * 10.0);
        var best = -1;
        var bestDiff = int.MaxValue;

        for (var i = 0; i < CtcssTonesHz.Length; i++)
        {
            var diff = Math.Abs(CtcssTonesHz[i] - target);
            if (diff >= bestDiff)
                continue;

            bestDiff = diff;
            best = i;
        }

        if (best < 0 || bestDiff > 5)
            return false;

        oneBasedIndex = best + ToneTableBase;
        return true;
    }

}
