namespace OscarWatch.Core.Radio;

/// <summary>
/// Yaesu FT-991 / FT-991A / FTX-1 ASCII CAT (newcat / Hamlib-compatible subset).
/// Frequencies are 9-digit Hz on VFO-A (FA) / VFO-B (FB).
/// </summary>
public static class YaesuFt991CatCodec
{
    public const int FrequencyDigits = 9;
    public const long MinFrequencyHz = 30_000;
    public const long MaxFrequencyHz = 470_000_000;

    /// <summary>CTCSS tone table (Hamlib common_ctcss_list / FT-991 manual Table 1).</summary>
    public static readonly double[] CtcssTonesHz =
    [
        67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5,
        94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3,
        131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 162.2, 167.9, 173.8, 179.9,
        186.2, 192.8, 203.5, 210.7, 218.1, 225.7, 233.6, 241.8, 250.3, 254.1
    ];

    public static string BuildReadFrequencyCommand(bool vfoB) =>
        vfoB ? "FB;" : "FA;";

    public static string BuildSetFrequencyCommand(bool vfoB, long hz)
    {
        if (hz < MinFrequencyHz || hz > MaxFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(hz));

        return vfoB ? $"FB{hz:D9};" : $"FA{hz:D9};";
    }

    /// <summary>P1=0 MAIN RX on VFO-A.</summary>
    public static string BuildSetModeCommand(char modeCode) => $"MD0{modeCode};";

    /// <summary>FT P1=2 TX on VFO-A, P1=3 TX on VFO-B (split).</summary>
    public static string BuildSetTxVfoCommand(bool txOnVfoB) => txOnVfoB ? "FT3;" : "FT2;";

    /// <summary>Copy VFO-A frequency and mode to VFO-B.</summary>
    public static string BuildCopyVfoAToBCommand() => "AB;";

    public static string BuildDialLockCommand(bool on) => on ? "LK1;" : "LK0;";

    public static string BuildCtcssOffCommand() => "CT00;";

    public static string BuildCtcssEncodeCommand(int zeroBasedIndex) =>
        $"CN00{zeroBasedIndex:D3};CT02;";

    public static string BuildCtcssSquelchCommand(int zeroBasedIndex) =>
        $"CN0{zeroBasedIndex:D3};CT01;";

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

    public static bool TryGetModeCode(string mode, out char modeCode)
    {
        modeCode = default;
        var upper = mode.Trim().ToUpperInvariant();
        var code = upper switch
        {
            "LSB" or "DATA-LSB" => '1',
            "USB" or "DATA-USB" => '2',
            "CW" or "CW-U" => '3',
            "CWR" or "CW-L" => '7',
            "FM" or "DATA-FM" or "FM-DATA" => '4',
            "FMN" => 'B',
            "AM" => '5',
            "AMN" => 'D',
            _ => (char)0
        };

        if (code == 0)
            return false;

        modeCode = code;
        return true;
    }

    public static bool IsFmMode(string mode)
    {
        var upper = mode.Trim().ToUpperInvariant();
        return upper is "FM" or "FMN" or "DATA-FM" or "FM-DATA" or "C4FM";
    }

    /// <summary>Read set RF power (watts). Answer is <c>PCxxx;</c> with xxx = 005–100.</summary>
    public static string BuildReadPowerCommand() => "PC;";

    /// <summary>Set RF power. <c>PCxxx;</c> with xxx = 005–100.</summary>
    public static bool TryBuildSetPowerCommand(double watts, out string command)
    {
        command = "";
        var rounded = (int)Math.Round(watts);
        if (rounded is < 5 or > 100)
            return false;

        command = $"PC{rounded:D3};";
        return true;
    }

    /// <summary>Parse <c>PCxxx;</c> (or bare digits) into watts.</summary>
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

    public static bool TryGetCtcssIndex(double toneHz, out int zeroBasedIndex)
    {
        zeroBasedIndex = 0;
        var best = -1;
        var bestDiff = double.MaxValue;

        for (var i = 0; i < CtcssTonesHz.Length; i++)
        {
            var diff = Math.Abs(CtcssTonesHz[i] - toneHz);
            if (diff >= bestDiff)
                continue;

            bestDiff = diff;
            best = i;
        }

        if (best < 0 || bestDiff > 0.15)
            return false;

        zeroBasedIndex = best;
        return true;
    }
}
