using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class SpidRotatorCodecTests
{
    [Fact]
    public void BuildStatusCommand_matches_protocol_layout()
    {
        Span<byte> packet = stackalloc byte[SpidRotatorCodec.CommandLength];
        SpidRotatorCodec.BuildStatusCommand(packet);

        Assert.Equal(SpidRotatorCodec.StartByte, packet[0]);
        Assert.Equal(SpidRotatorCodec.CommandStatus, packet[11]);
        Assert.Equal(SpidRotatorCodec.EndByte, packet[12]);
        for (var i = 1; i < 11; i++)
            Assert.Equal(0, packet[i]);
    }

    [Fact]
    public void BuildSetCommand_rot2_matches_protocol_example()
    {
        Span<byte> packet = stackalloc byte[SpidRotatorCodec.CommandLength];
        SpidRotatorCodec.BuildSetCommand(packet, 123.5, 77.0, pulsesPerDegree: 2, rot1Mode: false);

        Assert.Equal(
            new byte[] { 0x57, 0x30, 0x39, 0x36, 0x37, 0x02, 0x30, 0x38, 0x37, 0x34, 0x02, 0x2F, 0x20 },
            packet.ToArray());
    }

    [Fact]
    public void BuildSetCommand_rot2_encodes_elevation_below_horizon()
    {
        Span<byte> packet = stackalloc byte[SpidRotatorCodec.CommandLength];
        SpidRotatorCodec.BuildSetCommand(packet, 180, -10, pulsesPerDegree: 2, rot1Mode: false);

        var encoded = (packet[6] - (byte)'0') * 1000
            + (packet[7] - (byte)'0') * 100
            + (packet[8] - (byte)'0') * 10
            + (packet[9] - (byte)'0');
        Assert.Equal(-10, encoded / 2.0 - 360.0);
    }

    [Fact]
    public void BuildSetCommand_rot1_encodes_whole_degrees()
    {
        Span<byte> packet = stackalloc byte[SpidRotatorCodec.CommandLength];
        SpidRotatorCodec.BuildSetCommand(packet, 123, 0, pulsesPerDegree: 1, rot1Mode: true);

        Assert.Equal(0x57, packet[0]);
        Assert.Equal((byte)'4', packet[1]);
        Assert.Equal((byte)'8', packet[2]);
        Assert.Equal((byte)'3', packet[3]);
        Assert.Equal((byte)'0', packet[4]);
        Assert.Equal(SpidRotatorCodec.CommandSet, packet[11]);
        Assert.Equal(SpidRotatorCodec.EndByte, packet[12]);
    }

    [Fact]
    public void TryParseRot2Status_matches_protocol_example()
    {
        var response = new byte[] { 0x57, 0x03, 0x07, 0x02, 0x05, 0x02, 0x03, 0x09, 0x04, 0x00, 0x02, 0x20 };

        Assert.True(SpidRotatorCodec.TryParseRot2Status(response, out var az, out var el, out var pulse));
        Assert.Equal(12.5, az);
        Assert.Equal(34.0, el);
        Assert.Equal(2, pulse);
    }

    [Fact]
    public void TryParseRot1Status_decodes_integer_azimuth()
    {
        var response = new byte[] { 0x57, 0x03, 0x07, 0x02, 0x20 };

        Assert.True(SpidRotatorCodec.TryParseRot1Status(response, out var az));
        Assert.Equal(12, az);
    }

    [Fact]
    public void TryParseRot2Status_rejects_out_of_range_digits()
    {
        var badAzimuth = new byte[] { 0x57, 0x03, 0x0A, 0x02, 0x05, 0x02, 0x03, 0x09, 0x04, 0x00, 0x02, 0x20 };
        var badElevation = new byte[] { 0x57, 0x03, 0x07, 0x02, 0x05, 0x02, 0x03, 0x39, 0x04, 0x00, 0x02, 0x20 };

        Assert.False(SpidRotatorCodec.TryParseRot2Status(badAzimuth, out _, out _, out _));
        Assert.False(SpidRotatorCodec.TryParseRot2Status(badElevation, out _, out _, out _));
    }

    [Fact]
    public void TryParseRot1Status_rejects_out_of_range_digits()
    {
        Assert.False(SpidRotatorCodec.TryParseRot1Status([0x57, 0x33, 0x07, 0x02, 0x20], out _));
    }

    [Fact]
    public void TryParseRot2Status_rejects_short_buffer()
    {
        Assert.False(SpidRotatorCodec.TryParseRot2Status([0x57, 0x03], out _, out _, out _));
    }

    [Fact]
    public void TryParseRot1Status_rejects_short_buffer()
    {
        Assert.False(SpidRotatorCodec.TryParseRot1Status([0x57], out _));
    }
}
