using System.Buffers.Binary;
using System.Text;

namespace OscarWatch.Core.PskReporter;

/// <summary>
/// Builds PSK Reporter IPFIX datagrams (https://pskreporter.info/pskdev.html).
/// Receiver: callsign, locator, decoding software, antenna.
/// Sender: callsign, frequency, SNR, mode, locator, information source, flow start seconds.
/// </summary>
public static class PskReporterPacketBuilder
{
    /// <summary>Keep datagrams under a typical path MTU.</summary>
    public const int MaxDatagramBytes = 1400;

    public const ushort ReceiverTemplateId = 0x9A12;
    public const ushort SenderTemplateId = 0x9A13;

    private const uint EnterpriseNumber = 30351;
    private const ushort VariableLength = 0xFFFF;
    private const byte AutomaticallyExtracted = 1;
    private const int HeaderBytes = 16;
    private const int SetHeaderBytes = 4;
    private const int MaxStringBytes = 254;

    private static readonly byte[] ReceiverTemplate = BuildReceiverTemplate();
    private static readonly byte[] SenderTemplate = BuildSenderTemplate();

    public static byte[] Build(
        PskReporterReceiver receiver,
        IReadOnlyList<PskReporterSpot> spots,
        bool includeTemplates,
        uint exportTimeSeconds,
        uint sequenceNumber,
        uint observationDomainId)
    {
        var body = new List<byte>(MaxDatagramBytes);
        if (includeTemplates)
        {
            body.AddRange(ReceiverTemplate);
            body.AddRange(SenderTemplate);
        }

        body.AddRange(BuildReceiverRecord(receiver));
        if (spots.Count > 0)
            body.AddRange(BuildSenderSet(spots));

        var packet = new byte[HeaderBytes + body.Count];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 0x000A);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], exportTimeSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], observationDomainId);
        body.CopyTo(packet, HeaderBytes);
        return packet;
    }

    /// <summary>Datagram size for this receiver and spot list, with templates (worst case).</summary>
    public static int EstimateSize(PskReporterReceiver receiver, IReadOnlyList<PskReporterSpot> spots)
    {
        var size = HeaderBytes + ReceiverTemplate.Length + SenderTemplate.Length
            + BuildReceiverRecord(receiver).Length;
        if (spots.Count == 0)
            return size;

        var records = SetHeaderBytes;
        foreach (var spot in spots)
            records += SenderRecordBytes(spot);
        return size + Pad4(records);
    }

    private static byte[] BuildReceiverTemplate()
    {
        var bytes = new List<byte>();
        WriteUInt16(bytes, 3); // options template set
        WriteUInt16(bytes, 0); // length, patched below
        WriteUInt16(bytes, ReceiverTemplateId);
        WriteUInt16(bytes, 4); // field count
        WriteUInt16(bytes, 1); // scope field count
        WriteEnterpriseField(bytes, 2, VariableLength); // receiverCallsign
        WriteEnterpriseField(bytes, 4, VariableLength); // receiverLocator
        WriteEnterpriseField(bytes, 8, VariableLength); // decodingSoftware
        WriteEnterpriseField(bytes, 9, VariableLength); // antennaInformation
        return FinishSet(bytes);
    }

    private static byte[] BuildSenderTemplate()
    {
        var bytes = new List<byte>();
        WriteUInt16(bytes, 2); // template set
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, SenderTemplateId);
        WriteUInt16(bytes, 7);
        WriteEnterpriseField(bytes, 1, VariableLength); // senderCallsign
        WriteEnterpriseField(bytes, 5, 4); // frequency
        WriteEnterpriseField(bytes, 6, 1); // sNR
        WriteEnterpriseField(bytes, 10, VariableLength); // mode
        WriteEnterpriseField(bytes, 3, VariableLength); // senderLocator
        WriteEnterpriseField(bytes, 11, 1); // informationSource
        WriteUInt16(bytes, 150); // flowStartSeconds (IANA)
        WriteUInt16(bytes, 4);
        return FinishSet(bytes);
    }

    private static byte[] BuildReceiverRecord(PskReporterReceiver receiver)
    {
        var bytes = new List<byte>();
        WriteUInt16(bytes, ReceiverTemplateId);
        WriteUInt16(bytes, 0);
        WriteString(bytes, receiver.Callsign);
        WriteString(bytes, receiver.Locator);
        WriteString(bytes, receiver.DecodingSoftware);
        WriteString(bytes, receiver.Antenna);
        return FinishSet(bytes);
    }

    private static byte[] BuildSenderSet(IReadOnlyList<PskReporterSpot> spots)
    {
        var bytes = new List<byte>();
        WriteUInt16(bytes, SenderTemplateId);
        WriteUInt16(bytes, 0);
        foreach (var spot in spots)
        {
            WriteString(bytes, spot.SenderCallsign);
            WriteUInt32(bytes, (uint)Math.Clamp(spot.FrequencyHz, 0, uint.MaxValue));
            bytes.Add(unchecked((byte)(sbyte)Math.Clamp(spot.SnrDb, sbyte.MinValue, sbyte.MaxValue)));
            WriteString(bytes, spot.Mode);
            WriteString(bytes, spot.SenderLocator ?? "");
            bytes.Add(AutomaticallyExtracted);
            WriteUInt32(bytes, ToUnixSeconds(spot.TimeUtc));
        }

        return FinishSet(bytes);
    }

    private static int SenderRecordBytes(PskReporterSpot spot) =>
        StringBytes(spot.SenderCallsign) + 4 + 1 + StringBytes(spot.Mode)
        + StringBytes(spot.SenderLocator ?? "") + 1 + 4;

    private static int StringBytes(string value) =>
        1 + Math.Min(Encoding.UTF8.GetByteCount(value), MaxStringBytes);

    /// <summary>Pads a set to a multiple of 4 bytes and writes its length at offset 2.</summary>
    private static byte[] FinishSet(List<byte> bytes)
    {
        while (bytes.Count % 4 != 0)
            bytes.Add(0);
        var result = bytes.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)result.Length);
        return result;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private static void WriteEnterpriseField(List<byte> bytes, ushort elementId, ushort length)
    {
        WriteUInt16(bytes, (ushort)(0x8000 | elementId));
        WriteUInt16(bytes, length);
        WriteUInt32(bytes, EnterpriseNumber);
    }

    private static void WriteString(List<byte> bytes, string value)
    {
        var data = Encoding.UTF8.GetBytes(value ?? "");
        var length = Math.Min(data.Length, MaxStringBytes);
        bytes.Add((byte)length);
        for (var i = 0; i < length; i++)
            bytes.Add(data[i]);
    }

    private static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void WriteUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    public static uint ToUnixSeconds(DateTime utc)
    {
        var value = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return (uint)Math.Clamp(seconds, 0, uint.MaxValue);
    }
}
