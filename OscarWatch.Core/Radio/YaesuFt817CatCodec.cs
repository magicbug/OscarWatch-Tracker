namespace OscarWatch.Core.Radio;

/// <summary>
/// Yaesu FT-817 / FT-818 five-byte CAT encoding (Hamlib / KA7OEI-compatible).
/// Frequency resolution is 10 Hz over CAT. Serial format: 8N2, five-byte blocks.
/// </summary>
public static class YaesuFt817CatCodec
{
    public const int CommandLength = 5;

    /// <summary>Dial/panel lock on (CAT opcode 0x00) — not a generic CAT session enable.</summary>
    public static ReadOnlySpan<byte> DialLockOn => [0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Dial/panel lock off (CAT opcode 0x80).</summary>
    public static ReadOnlySpan<byte> DialLockOff => [0x00, 0x00, 0x00, 0x00, 0x80];
    public static ReadOnlySpan<byte> SplitOn => [0x00, 0x00, 0x00, 0x00, 0x02];
    public static ReadOnlySpan<byte> SplitOff => [0x00, 0x00, 0x00, 0x00, 0x82];
    public static ReadOnlySpan<byte> PollFreqMode => [0x00, 0x00, 0x00, 0x00, 0x03];
    public static ReadOnlySpan<byte> ToggleVfo => [0x00, 0x00, 0x00, 0x00, 0x81];

    private static readonly int[] CtcssTonesHz =
    [
        670, 693, 719, 744, 770, 797, 825, 854, 885, 915,
        948, 974, 1000, 1035, 1072, 1109, 1148, 1188, 1230, 1273,
        1318, 1365, 1413, 1462, 1514, 1567, 1622, 1679, 1738, 1799,
        1862, 1928, 2035, 2107, 2181, 2257, 2336, 2418, 2503
    ];

    public static void EncodeFrequency10Hz(long hz, Span<byte> command)
    {
        if (command.Length != CommandLength)
            throw new ArgumentException($"Command span must be {CommandLength} bytes.", nameof(command));

        var units = hz / 10;
        for (var i = 3; i >= 0; i--)
        {
            var pair = (int)(units % 100);
            units /= 100;
            command[i] = (byte)((pair / 10 << 4) | (pair % 10));
        }
    }

    public static long DecodeFrequency10Hz(ReadOnlySpan<byte> response)
    {
        if (response.Length < 4)
            return 0;

        long units = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = response[i];
            units = units * 100 + (b >> 4) * 10 + (b & 0x0F);
        }

        return units * 10;
    }

    public static string DecodeMode(byte modeByte) => modeByte switch
    {
        0x00 => "LSB",
        0x01 => "USB",
        0x02 => "CW",
        0x03 => "CWR",
        0x04 => "AM",
        0x08 => "FM",
        0x88 => "FM",
        0x0A => "DIG",
        0x0C => "PKT",
        _ => "USB"
    };

    public static byte[] BuildSetFrequencyCommand(long hz)
    {
        var cmd = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01 };
        EncodeFrequency10Hz(hz, cmd);
        return cmd;
    }

    public static byte[] BuildSetModeCommand(string mode, bool narrow = false)
    {
        var modeByte = ResolveModeByte(mode, narrow);
        return [modeByte, 0x00, 0x00, 0x00, 0x07];
    }

    public static bool IsFmMode(string mode)
    {
        var upper = mode.Trim().ToUpperInvariant();
        return upper is "FM" or "FMN";
    }

    /// <summary>
    /// Encode CTCSS tone (0.1 Hz units) into the first two command bytes (KA7OEI / Hamlib ft817).
    /// </summary>
    public static void EncodeCtcssTone10Hz(int toneTenthsHz, Span<byte> command)
    {
        if (command.Length < 2)
            throw new ArgumentException("Command span must include at least two bytes.", nameof(command));

        if (toneTenthsHz is < 0 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(toneTenthsHz));

        var hundreds = toneTenthsHz / 100;
        var tens = toneTenthsHz % 100;
        command[0] = (byte)(((hundreds / 10) << 4) | (hundreds % 10));
        command[1] = (byte)(((tens / 10) << 4) | (tens % 10));
    }

    public static byte[] BuildCtcssFrequencyCommand(double toneHz)
    {
        if (!IsSupportedCtcssTone(toneHz))
            throw new ArgumentOutOfRangeException(nameof(toneHz), "CTCSS tone not supported by FT-817.");

        var toneTenths = (int)Math.Round(toneHz * 10);
        var cmd = new byte[5];
        EncodeCtcssTone10Hz(toneTenths, cmd);
        cmd[2] = cmd[0];
        cmd[3] = cmd[1];
        cmd[4] = 0x0b;
        return cmd;
    }

    public static byte[] BuildCtcssOnCommand(bool encoderOnly) =>
        [(encoderOnly ? (byte)0x4a : (byte)0x2a), 0x00, 0x00, 0x00, 0x0a];

    public static byte[] BuildCtcssOffCommand() => [0x8a, 0x00, 0x00, 0x00, 0x0a];

    public static bool IsSupportedCtcssTone(double toneHz)
    {
        var target = (int)Math.Round(toneHz * 10);
        for (var i = 0; i < CtcssTonesHz.Length; i++)
        {
            if (CtcssTonesHz[i] == target)
                return true;
        }

        return false;
    }

    private static byte ResolveModeByte(string mode, bool narrow)
    {
        var upper = mode.Trim().ToUpperInvariant();
        var useNarrow = upper switch
        {
            "FM" => false,
            "FMN" => true,
            _ => narrow
        };

        // DATA-USB / DATA-LSB → DIG (0x0A): rear DATA jack. Sideband is menu #26 (USER-U / USER-L).
        // DATA-FM / FM-DATA → PKT (0x0C): packet FM on the DATA jack.
        // Plain USB / LSB / FM stay on the mic jack.
        return upper switch
        {
            "LSB" => 0x00,
            "USB" => 0x01,
            "DATA-LSB" or "DATA-USB" => 0x0A,
            "CW" => useNarrow ? (byte)0x82 : (byte)0x02,
            "CWR" => useNarrow ? (byte)0x83 : (byte)0x03,
            "AM" => useNarrow ? (byte)0x84 : (byte)0x04,
            "FM" or "FMN" => useNarrow ? (byte)0x88 : (byte)0x08,
            "DATA-FM" or "FM-DATA" => 0x0C,
            _ => 0x01
        };
    }
}
