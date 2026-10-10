namespace OscarWatch.Core.Radio;

/// <summary>CI-V frame encoding/decoding (ICOM CAT).</summary>
public static class IcomCivCodec
{
    public static byte[] BuildCommandFrame(int civAddress, ReadOnlySpan<byte> commandBody)
    {
        var frame = new byte[4 + commandBody.Length + 1];
        frame[0] = 0xFE;
        frame[1] = 0xFE;
        frame[2] = (byte)civAddress;
        frame[3] = 0x00;
        commandBody.CopyTo(frame.AsSpan(4));
        frame[^1] = 0xFD;
        return frame;
    }

    public static byte[] EncodeSetFrequencyHz(long hz)
    {
        // 5 BCD bytes cover up to 9.999 GHz; IC-905 10 GHz needs a 6th (10 GHz) digit pair.
        var digitCount = hz >= 10_000_000_000L ? 12 : 10;
        var s = hz.ToString().PadLeft(digitCount, '0');
        if (s.Length > digitCount)
            s = s[^digitCount..];

        if (digitCount == 12)
        {
            return
            [
                0x05,
                Convert.ToByte(s[10..12], 16),
                Convert.ToByte(s[8..10], 16),
                Convert.ToByte(s[6..8], 16),
                Convert.ToByte(s[4..6], 16),
                Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[0..2], 16)
            ];
        }

        return
        [
            0x05,
            Convert.ToByte(s[8..10], 16),
            Convert.ToByte(s[6..8], 16),
            Convert.ToByte(s[4..6], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[0..2], 16)
        ];
    }

    /// <summary>
    /// Decodes a 0x03 read-frequency response. Bytes are BCD digit pairs; the digit string is
    /// a decimal Hz value (ICOM BCD digit pairs), not a hexadecimal number.
    /// Supports 5-byte (to ~10 GHz) and 6-byte (IC-905 10 GHz) payloads.
    /// </summary>
    public static long? DecodeFrequencyFromResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 10)
            return null;

        // FE FE addr to cmd + N BCD bytes + trailer(s). Prefer 6 BCD when present.
        var freqLen = response.Length >= 13 ? 6 : 5;
        ReadOnlySpan<byte> freqBytes;
        if (response.Length >= 5 + freqLen)
            freqBytes = response.Slice(5, freqLen);
        else if (response.Length >= freqLen + 1)
            freqBytes = response[^(freqLen + 1)..^1];
        else
            return null;

        var digits = "";
        for (var i = freqBytes.Length - 1; i >= 0; i--)
            digits += freqBytes[i].ToString("X2");

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return 0;

        return long.TryParse(digits, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var hz)
            ? hz
            : null;
    }

    /// <summary>CI-V body to read RF power (command 0x14 sub 0x0A).</summary>
    public static byte[] EncodeReadRfPowerCommand() => [0x14, 0x0A];

    /// <summary>CI-V body to set RF power (command 0x14 sub 0x0A, level 0–255).</summary>
    public static byte[] EncodeWriteRfPowerCommand(int level0To255)
    {
        var (high, low) = EncodeLevel255(level0To255);
        return [0x14, 0x0A, high, low];
    }

    /// <summary>
    /// Decodes a 0x14 0x0A RF-power response. Data is four BCD digits (0000–0255)
    /// packed into two bytes (minimum to maximum RF power setting).
    /// </summary>
    public static int? DecodeLevel255FromResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
            return null;

        // Prefer FE FE … 14 0A d0 d1 … FD
        for (var i = 0; i < response.Length - 3; i++)
        {
            if (response[i] != 0x14 || response[i + 1] != 0x0A)
                continue;
            if (i + 3 >= response.Length)
                break;
            return DecodePackedBcd255(response[i + 2], response[i + 3]);
        }

        return null;
    }

    /// <summary>Encode a 0–255 level as two packed-BCD bytes (0000–0255).</summary>
    public static (byte High, byte Low) EncodeLevel255(int level)
    {
        var clamped = Math.Clamp(level, 0, 255);
        var thousands = clamped / 1000;
        var hundreds = (clamped / 100) % 10;
        var tens = (clamped / 10) % 10;
        var ones = clamped % 10;
        return ((byte)((thousands << 4) | hundreds), (byte)((tens << 4) | ones));
    }

    private static int? DecodePackedBcd255(byte high, byte low)
    {
        var d0 = (high >> 4) & 0xF;
        var d1 = high & 0xF;
        var d2 = (low >> 4) & 0xF;
        var d3 = low & 0xF;
        if (d0 > 9 || d1 > 9 || d2 > 9 || d3 > 9)
            return null;

        var value = d0 * 1000 + d1 * 100 + d2 * 10 + d3;
        return value is >= 0 and <= 255 ? value : null;
    }

    /// <summary>
    /// True when <paramref name="hz"/> is in an amateur satellite band on ICOM satellite rigs.
    /// Includes HF (IC-9100), VHF/UHF/23cm (IC-910/9700/9100), and SHF for IC-905
    /// (13 cm / 6 cm / 3 cm).
    /// </summary>
    public static bool IsValidSatelliteFrequencyHz(long hz) =>
        hz is >= 1_800_000 and <= 54_000_000
            or >= 144_000_000 and <= 148_000_000
            or >= 430_000_000 and <= 450_000_000
            or >= 1_200_000_000 and <= 1_300_000_000
            or >= 2_300_000_000 and <= 2_450_000_000
            or >= 5_650_000_000 and <= 5_850_000_000
            or >= 10_000_000_000 and <= 10_500_000_000;

    public static int ParseCivAddressHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return 0x60;
        var s = hex.Trim().TrimStart('0', 'x', 'X');
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var addr)
            ? addr
            : 0x60;
    }

    /// <summary>CI-V SET mode (0x06) body for ICOM rigs. Returns null when unsupported.</summary>
    public static byte[]? EncodeSetModeCommand(string mode) =>
        mode.Trim().ToUpperInvariant() switch
        {
            "FM" or "FMN" or "DATA-FM" or "FM-DATA" => [0x06, 0x05],
            "USB" or "DATA-USB" => [0x06, 0x01],
            "LSB" or "DATA-LSB" => [0x06, 0x00],
            "CW" => [0x06, 0x03],
            _ => null
        };

    /// <summary>
    /// IC-910 SET mode: FM uses filter width byte (1 = wide, 2 = narrow).
    /// Hamlib <c>ic910_r2i_mode</c> / SatPC32 undocumented FM-N path — plain <c>06 05</c> leaves wide FM.
    /// </summary>
    public static byte[]? EncodeIc910SetModeCommand(string mode)
    {
        var normalized = TransponderCatModes.Normalize(mode);
        return normalized switch
        {
            "FMN" => [0x06, 0x05, 0x02],
            "FM" or "DATA-FM" => [0x06, 0x05, 0x01],
            _ => EncodeSetModeCommand(normalized)
        };
    }

    /// <summary>
    /// IC-9700 SATL mode sequence: base mode (0x06) plus DATA on/off (0x1A/0x06).
    /// Command 0x26 is unavailable in satellite mode; Hamlib uses this fallback path.
    /// </summary>
    public static byte[][] Encode9700SetModeCommands(string mode)
    {
        var normalized = TransponderCatModes.Normalize(mode);
        var baseMode = normalized switch
        {
            "DATA-USB" => "USB",
            "DATA-LSB" => "LSB",
            "DATA-FM" => "FM",
            _ => normalized
        };

        if (EncodeSetModeCommand(baseMode) is not { } baseCmd)
            return [];

        if (normalized is "DATA-USB" or "DATA-LSB" or "DATA-FM")
            return [baseCmd, [0x1A, 0x06, 0x01, 0x01]];

        if (baseMode is "USB" or "LSB")
            return [baseCmd, [0x1A, 0x06, 0x00, 0x00]];

        return [baseCmd];
    }

    public static byte[] EncodeToneHz(double hz, bool squelchTone)
    {
        var hertz = ((int)Math.Round(hz * 10)).ToString();
        if (int.Parse(hertz) >= 1000)
            return [(byte)(squelchTone ? 0x1B : 0x1B), (byte)(squelchTone ? 0x01 : 0x00),
                Convert.ToByte("1" + hertz[1], 16), Convert.ToByte(hertz[2..4], 16)];
        return [(byte)(squelchTone ? 0x1B : 0x1B), (byte)(squelchTone ? 0x01 : 0x00),
            Convert.ToByte("0" + hertz[0], 16), Convert.ToByte(hertz[1..3], 16)];
    }
}
