using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Core.Cw;

/// <summary>
/// Icom CW by CI-V: command <c>0x17</c> sends up to 30 characters that the radio keys itself,
/// and <c>0x17 0xFF</c> stops. Key speed is level <c>0x14 0x0C</c> (0000 is 6 WPM, 0255 is 48 WPM).
/// </summary>
public static class IcomCwCodec
{
    public const byte SendCommand = 0x17;
    public const byte StopByte = 0xFF;
    public const int MaxCharacters = 30;
    public const int MinWpm = 6;
    public const int MaxWpm = 48;

    /// <summary>Icom radios in OscarWatch whose CI-V accepts command 0x17.</summary>
    public static bool SupportsTextKeying(RigType rigType) => rigType is
        RigType.IcomIc705 or RigType.IcomIc7100 or RigType.IcomIc7300
        or RigType.IcomIc905 or RigType.IcomIc9700;

    /// <summary>One <c>0x17</c> body per piece of the message.</summary>
    public static IReadOnlyList<(byte[] Body, CwMessage Piece)> BuildSendCommands(CwMessage message) =>
        CwTextChunker.Split(message, MaxCharacters, SymbolWidth)
            .Select(piece => (BuildSendCommand(piece), piece))
            .ToList();

    public static byte[] BuildSendCommand(CwMessage piece)
    {
        var text = new StringBuilder();
        foreach (var symbol in piece.Symbols)
            text.Append(MapSymbol(symbol));

        var body = new byte[1 + text.Length];
        body[0] = SendCommand;
        for (var i = 0; i < text.Length; i++)
            body[i + 1] = (byte)text[i];
        return body;
    }

    public static byte[] BuildStopCommand() => [SendCommand, StopByte];

    public static byte[] BuildSpeedCommand(int wpm)
    {
        var (high, low) = IcomCivCodec.EncodeLevel255(SpeedLevel(wpm));
        return [0x14, 0x0C, high, low];
    }

    /// <summary>CI-V key-speed level for <paramref name="wpm"/>, clamped to 6 to 48 WPM.</summary>
    public static int SpeedLevel(int wpm)
    {
        var clamped = Math.Clamp(wpm, MinWpm, MaxWpm);
        return (int)Math.Round((clamped - MinWpm) * 255.0 / (MaxWpm - MinWpm), MidpointRounding.AwayFromZero);
    }

    /// <summary>A <c>^</c> joins the next two letters with no gap, so <c>^SK</c> is the SK prosign.</summary>
    private static string MapSymbol(CwSymbol symbol)
    {
        if (!symbol.IsProsign)
            return symbol.Value;

        return symbol.Value switch
        {
            "AR" => "+",
            "BT" => "=",
            _ => "^" + symbol.Value
        };
    }

    private static int SymbolWidth(CwSymbol symbol) => MapSymbol(symbol).Length;
}
