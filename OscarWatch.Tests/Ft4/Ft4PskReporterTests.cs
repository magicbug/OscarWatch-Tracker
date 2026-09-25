using System.Buffers.Binary;
using System.Text;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
using OscarWatch.Core.PskReporter;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4PskReporterTests
{
    private const long UplinkHz = 435_640_000;
    private const long DownlinkHz = 145_940_000;
    private static readonly DateTime Slot = new(2026, 9, 25, 17, 30, 7, 500, DateTimeKind.Utc);

    private static LiveTrackerSnapshot Snapshot(long uplinkHz = UplinkHz) =>
        new("RS-44", "LSB", "USB", uplinkHz, DownlinkHz, "70cm", "2m", "Linear");

    private static Ft4DecodedMessage Decode(string text, bool own = false, bool tx = false)
    {
        Ft4MessageCodec.TryParse(text, out var to, out var de, out var extra);
        return new Ft4DecodedMessage(Slot, text, 1500, 0.1f, -7.4f, to, de, extra, own, tx);
    }

    private static PskReporterReceiver Receiver(string sat = "RS-44") =>
        new("MM9SQL", "IO85ju", "OscarWatch 1.0.0", sat);

    [Fact]
    public void Spot_frequency_is_uplink_never_downlink()
    {
        Assert.True(Ft4PskReporterSpots.TryCreateSpot(Decode("CQ W1AW FN31"), Snapshot(), out var spot));
        Assert.Equal(UplinkHz, spot.FrequencyHz);
        Assert.NotEqual(DownlinkHz, spot.FrequencyHz);
        Assert.Equal("W1AW", spot.SenderCallsign);
        Assert.Equal("FT4", spot.Mode);
        Assert.Equal(-7, spot.SnrDb);
    }

    [Fact]
    public void Spot_dropped_when_uplink_missing()
    {
        Assert.False(Ft4PskReporterSpots.TryCreateSpot(Decode("CQ W1AW FN31"), Snapshot(uplinkHz: 0), out _));
        Assert.False(Ft4PskReporterSpots.TryCreateSpot(Decode("CQ W1AW FN31"), LiveTrackerSnapshot.Empty, out _));
    }

    [Fact]
    public void Own_transmit_and_echo_are_not_spotted()
    {
        Assert.False(Ft4PskReporterSpots.TryCreateSpot(Decode("CQ MM9SQL IO85", own: true), Snapshot(), out _));
        Assert.False(Ft4PskReporterSpots.TryCreateSpot(Decode("CQ MM9SQL IO85", tx: true), Snapshot(), out _));
    }

    [Theory]
    [InlineData("CQ W1AW FN31", "FN31")]
    [InlineData("MM9SQL W1AW FN31", "FN31")]
    [InlineData("MM9SQL W1AW -12", null)]
    [InlineData("MM9SQL W1AW RR73", null)]
    [InlineData("MM9SQL W1AW 73", null)]
    public void Grid_attached_only_for_locator_token(string text, string? expected)
    {
        Assert.True(Ft4PskReporterSpots.TryCreateSpot(Decode(text), Snapshot(), out var spot));
        Assert.Equal(expected, spot.SenderLocator);
    }

    [Theory]
    [InlineData("CQ DX W1AW FN31")]
    [InlineData("CQ POTA W1AW")]
    public void Cq_modifier_is_not_treated_as_a_callsign(string text) =>
        Assert.False(Ft4PskReporterSpots.TryCreateSpot(Decode(text), Snapshot(), out _));

    [Fact]
    public void Receiver_antenna_is_satellite_name()
    {
        Assert.True(Ft4PskReporterSpots.TryCreateReceiver("mm9sql", "IO85JU", "RS-44", "OscarWatch 1.0.0", out var receiver));
        Assert.Equal("MM9SQL", receiver.Callsign);
        Assert.Equal("IO85ju", receiver.Locator);
        Assert.Equal("RS-44", receiver.Antenna);
    }

    [Fact]
    public void Receiver_requires_callsign_and_grid()
    {
        Assert.False(Ft4PskReporterSpots.TryCreateReceiver("", "IO85ju", "RS-44", "x", out _));
        Assert.False(Ft4PskReporterSpots.TryCreateReceiver("MM9SQL", "", "RS-44", "x", out _));
    }

    [Fact]
    public void Templates_match_published_layout()
    {
        var spot = new PskReporterSpot("W1AW", "FN31", UplinkHz, -7, "FT4", Slot);
        var packet = PskReporterPacketBuilder.Build(Receiver(), [spot], includeTemplates: true, 1_790_000_000, 0, 42);

        Assert.Equal(0x000A, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12)));

        // Receiver options template: set 3, length 0x2C, four fields with one scope field.
        var receiverTemplate = packet.AsSpan(16, 0x2C);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(receiverTemplate));
        Assert.Equal(0x2C, BinaryPrimitives.ReadUInt16BigEndian(receiverTemplate[2..]));
        Assert.Equal(PskReporterPacketBuilder.ReceiverTemplateId, BinaryPrimitives.ReadUInt16BigEndian(receiverTemplate[4..]));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(receiverTemplate[6..]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(receiverTemplate[8..]));
        Assert.Equal(new byte[] { 0x80, 0x09, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F }, receiverTemplate.Slice(34, 8).ToArray());

        // Sender template: set 2, length 0x3C, seven fields ending with flowStartSeconds.
        var senderTemplate = packet.AsSpan(16 + 0x2C, 0x3C);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(senderTemplate));
        Assert.Equal(0x3C, BinaryPrimitives.ReadUInt16BigEndian(senderTemplate[2..]));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(senderTemplate[6..]));
        Assert.Equal(new byte[] { 0x00, 0x96, 0x00, 0x04 }, senderTemplate[^4..].ToArray());
    }

    [Fact]
    public void Records_carry_uplink_antenna_and_padding()
    {
        var spot = new PskReporterSpot("W1AW", null, UplinkHz, -7, "FT4", Slot);
        var packet = PskReporterPacketBuilder.Build(Receiver(), [spot], includeTemplates: false, 1_790_000_000, 5, 1);

        var receiverSet = packet.AsSpan(16);
        var receiverLength = BinaryPrimitives.ReadUInt16BigEndian(receiverSet[2..]);
        Assert.Equal(PskReporterPacketBuilder.ReceiverTemplateId, BinaryPrimitives.ReadUInt16BigEndian(receiverSet));
        Assert.Equal(0, receiverLength % 4);
        Assert.Contains("RS-44", Encoding.ASCII.GetString(receiverSet[..receiverLength]));

        var senderSet = receiverSet[receiverLength..];
        Assert.Equal(PskReporterPacketBuilder.SenderTemplateId, BinaryPrimitives.ReadUInt16BigEndian(senderSet));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(senderSet[2..]) % 4);

        // 04 'W1AW', then 4-byte frequency, then SNR byte.
        Assert.Equal(4, senderSet[4]);
        Assert.Equal("W1AW", Encoding.ASCII.GetString(senderSet.Slice(5, 4)));
        Assert.Equal((uint)UplinkHz, BinaryPrimitives.ReadUInt32BigEndian(senderSet[9..]));
        Assert.Equal(unchecked((byte)(sbyte)-7), senderSet[13]);
        Assert.Equal(packet.Length, 16 + receiverLength + BinaryPrimitives.ReadUInt16BigEndian(senderSet[2..]));
    }

    [Fact]
    public void Client_drops_spots_while_off_and_discards_on_disable()
    {
        var sink = new MemoryTransport();
        var now = Slot;
        using var client = new PskReporterClient((_, _) => sink, () => now);

        client.Enqueue(Receiver(), Spot("W1AW"));
        Assert.Equal(0, client.PendingCount);

        client.Configure(true, null, 0);
        client.Enqueue(Receiver(), Spot("W1AW"));
        Assert.Equal(1, client.PendingCount);

        client.Configure(false, null, 0);
        Assert.Equal(0, client.PendingCount);
        Assert.True(sink.Disposed);
        Assert.Empty(sink.Datagrams);
    }

    [Fact]
    public void Client_batches_until_five_minutes_and_sends_templates_first_three_times()
    {
        var sink = new MemoryTransport();
        var now = Slot;
        using var client = new PskReporterClient((_, _) => sink, () => now);
        client.Configure(true, null, 0);

        client.Enqueue(Receiver(), Spot("W1AW"));
        client.Enqueue(Receiver(), Spot("W1AW"));
        client.Enqueue(Receiver(), Spot("K1ABC"));
        client.FlushIfDue();
        Assert.Empty(sink.Datagrams);

        for (var i = 0; i < 4; i++)
        {
            now += TimeSpan.FromMinutes(6);
            client.Enqueue(Receiver(), Spot("W1AW"));
            client.FlushIfDue();
        }

        Assert.Equal(4, sink.Datagrams.Count);
        Assert.Equal(2, CountSenderRecords(sink.Datagrams[0]));
        Assert.True(HasTemplates(sink.Datagrams[0]));
        Assert.True(HasTemplates(sink.Datagrams[2]));
        Assert.False(HasTemplates(sink.Datagrams[3]));
    }

    [Fact]
    public void Satellite_change_flushes_so_one_datagram_holds_one_receiver()
    {
        var sink = new MemoryTransport();
        using var client = new PskReporterClient((_, _) => sink, () => Slot);
        client.Configure(true, null, 0);

        client.Enqueue(Receiver("RS-44"), Spot("W1AW"));
        client.Enqueue(Receiver("SO-50"), Spot("K1ABC"));

        Assert.Single(sink.Datagrams);
        Assert.Contains("RS-44", Encoding.ASCII.GetString(sink.Datagrams[0]));
        Assert.DoesNotContain("SO-50", Encoding.ASCII.GetString(sink.Datagrams[0]));
    }

    [Fact]
    public void Defaults_are_off_and_production()
    {
        var settings = new Ft4Settings();
        Assert.False(settings.PskReporterEnabled);
        Assert.Equal("report.pskreporter.info", settings.PskReporterHost);
        Assert.Equal(4739, settings.PskReporterPort);
    }

    private static PskReporterSpot Spot(string call) => new(call, null, UplinkHz, -10, "FT4", Slot);

    private static bool HasTemplates(byte[] datagram) =>
        BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(16)) == 3;

    private static int CountSenderRecords(byte[] datagram)
    {
        var offset = 16;
        while (offset < datagram.Length)
        {
            var setId = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset));
            var setLength = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset + 2));
            if (setId != PskReporterPacketBuilder.SenderTemplateId)
            {
                offset += setLength;
                continue;
            }

            var end = offset + setLength;
            var pos = offset + 4;
            var count = 0;
            // Record: call, frequency (4), SNR (1), mode, locator, source (1), time (4). At least 14 bytes.
            while (end - pos >= 14)
            {
                pos += 1 + datagram[pos] + 4 + 1;
                pos += 1 + datagram[pos];
                pos += 1 + datagram[pos];
                pos += 1 + 4;
                count++;
            }

            return count;
        }

        return 0;
    }

    private sealed class MemoryTransport : IPskReporterTransport
    {
        public List<byte[]> Datagrams { get; } = [];
        public bool Disposed { get; private set; }

        public void Send(byte[] datagram) => Datagrams.Add(datagram);

        public void Dispose() => Disposed = true;
    }
}
