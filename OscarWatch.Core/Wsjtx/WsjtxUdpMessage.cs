using System.Text;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Wsjtx;

/// <summary>
/// Builds WSJT-X network datagrams (schema 3, big-endian Qt types).
/// See WSJT-X <c>NetworkMessage.hpp</c>.
/// </summary>
public static class WsjtxUdpMessage
{
    public const uint Magic = 0xADBCCBDA;
    public const uint Schema = 3;
    public const string ClientId = "OscarWatch";

    public const uint HeartbeatType = 0;
    public const uint QsoLoggedType = 5;
    public const uint CloseType = 6;
    public const uint LoggedAdifType = 12;

    /// <summary>Qt timespec value for UTC. No offset or time-zone bytes follow.</summary>
    private const byte UtcTimeSpec = 1;

    public static byte[] Heartbeat(string version, string? revision = null)
    {
        var buffer = Begin(HeartbeatType);
        WriteUInt32(buffer, Schema);
        WriteUtf8(buffer, version ?? "");
        WriteUtf8(buffer, revision ?? "");
        return buffer.ToArray();
    }

    public static byte[] Close() => Begin(CloseType).ToArray();

    public static byte[] QsoLogged(QsoLogbook logbook, QsoRecord qso)
    {
        var when = QsoLogbookTime.NormalizeToUtc(qso.QsoUtc);
        var call = MaidenheadLocator.NormalizeCallsign(qso.Call);
        var grid = MaidenheadLocator.NormalizeGrids(qso.GridSquare);
        var myCall = MaidenheadLocator.NormalizeCallsign(logbook.MyCallsign);
        var myGrid = MaidenheadLocator.NormalizeGrids(logbook.MyGridSquare);
        var mode = (qso.Mode ?? "").Trim();
        var prop = string.IsNullOrWhiteSpace(qso.PropMode) ? "SAT" : qso.PropMode.Trim();
        var hz = qso.FreqHz < 0 ? 0UL : (ulong)qso.FreqHz;

        var buffer = Begin(QsoLoggedType);
        WriteDateTimeUtc(buffer, when);
        WriteUtf8(buffer, call);
        WriteUtf8(buffer, grid);
        WriteUInt64(buffer, hz);
        WriteUtf8(buffer, mode);
        WriteUtf8(buffer, (qso.RstSent ?? "").Trim());
        WriteUtf8(buffer, (qso.RstRcvd ?? "").Trim());
        WriteUtf8(buffer, "");
        WriteUtf8(buffer, (qso.Comment ?? "").Trim());
        WriteUtf8(buffer, (qso.Name ?? "").Trim());
        WriteDateTimeUtc(buffer, when);
        WriteUtf8(buffer, myCall);
        WriteUtf8(buffer, myCall);
        WriteUtf8(buffer, myGrid);
        WriteUtf8(buffer, "");
        WriteUtf8(buffer, "");
        WriteUtf8(buffer, prop);
        return buffer.ToArray();
    }

    public static byte[] LoggedAdif(QsoLogbook logbook, QsoRecord qso)
    {
        var buffer = Begin(LoggedAdifType);
        WriteUtf8(buffer, BuildAdifText(logbook, qso));
        return buffer.ToArray();
    }

    public static string BuildAdifText(QsoLogbook logbook, QsoRecord qso)
    {
        var sb = new StringBuilder();
        AdifExporter.AppendField(sb, "ADIF_VER", "3.1.4");
        AdifExporter.AppendField(sb, "PROGRAMID", "OscarWatch");
        var version = typeof(WsjtxUdpMessage).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        AdifExporter.AppendField(sb, "PROGRAMVERSION", version);
        sb.AppendLine("<EOH>");
        sb.Append(AdifExporter.ExportRecord(logbook, qso));
        return sb.ToString();
    }

    /// <summary>Qt proleptic Gregorian Julian day, matching <c>QDate</c>.</summary>
    public static long JulianDay(int year, int month, int day)
    {
        if (year < 0)
            year++;

        var a = (14 - month) / 12;
        var y = (long)year + 4800 - a;
        var m = month + 12 * a - 3;
        return day + (153L * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    }

    private static List<byte> Begin(uint type)
    {
        var buffer = new List<byte>(256);
        WriteUInt32(buffer, Magic);
        WriteUInt32(buffer, Schema);
        WriteUInt32(buffer, type);
        WriteUtf8(buffer, ClientId);
        return buffer;
    }

    private static void WriteDateTimeUtc(List<byte> buffer, DateTime utc)
    {
        WriteInt64(buffer, JulianDay(utc.Year, utc.Month, utc.Day));
        var msecs = (uint)((utc.Hour * 3600 + utc.Minute * 60 + utc.Second) * 1000 + utc.Millisecond);
        WriteUInt32(buffer, msecs);
        buffer.Add(UtcTimeSpec);
    }

    private static void WriteUtf8(List<byte> buffer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUInt32(buffer, 0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(buffer, (uint)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteUInt64(List<byte> buffer, ulong value)
    {
        WriteUInt32(buffer, (uint)(value >> 32));
        WriteUInt32(buffer, (uint)value);
    }

    private static void WriteInt64(List<byte> buffer, long value) =>
        WriteUInt64(buffer, unchecked((ulong)value));
}
