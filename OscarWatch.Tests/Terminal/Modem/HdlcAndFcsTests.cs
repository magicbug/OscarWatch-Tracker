using System.Text;
using OscarWatch.Core.Terminal.Modem;

namespace OscarWatch.Tests.Terminal.Modem;

public sealed class HdlcAndFcsTests
{
    [Fact]
    public void Fcs_matches_the_x25_check_value()
    {
        Assert.Equal(0x906E, Fcs16.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Fcs_is_appended_low_byte_first_and_checks()
    {
        var data = Encoding.ASCII.GetBytes("123456789");
        var framed = Fcs16.Append(data);

        Assert.Equal(0x6E, framed[^2]);
        Assert.Equal(0x90, framed[^1]);
        Assert.True(Fcs16.Check(framed));

        framed[3] ^= 0x10;
        Assert.False(Fcs16.Check(framed));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    [InlineData(0x7E)]
    [InlineData(0x3F)]
    public void Stuffing_round_trips_awkward_bytes(byte fill)
    {
        var frame = ModemTestSignals.UiFrame("x");
        var payload = frame.Concat(Enumerable.Repeat(fill, 40)).ToArray();

        var decoded = DecodeBits(HdlcEncoder.EncodeBits([payload], 3, 2));

        var single = Assert.Single(decoded);
        Assert.Equal(Fcs16.Append(payload), single);
    }

    [Fact]
    public void Stuffed_bits_never_leave_six_ones_inside_a_frame()
    {
        var payload = Enumerable.Repeat((byte)0xFF, 30).ToArray();
        var bits = HdlcEncoder.EncodeBits([payload], 1, 1);

        // Skip the opening and closing flag.
        var body = bits.Skip(8).Take(bits.Count - 16).ToList();
        var run = 0;
        foreach (var b in body)
        {
            run = b ? run + 1 : 0;
            Assert.True(run <= 5);
        }
    }

    [Fact]
    public void Nrzi_changes_tone_on_zero_and_holds_on_one()
    {
        var tones = HdlcEncoder.ToTones([true, false, false, true, false], startMark: true);

        Assert.Equal([true, false, true, true, false], tones);
    }

    [Fact]
    public void Back_to_back_frames_share_flags_and_all_decode()
    {
        var frames = Enumerable.Range(0, 4).Select(i => ModemTestSignals.UiFrame($"frame {i}")).ToList();

        var decoded = DecodeBits(HdlcEncoder.EncodeBits(frames, 2, 2));

        Assert.Equal(frames.Select(f => Fcs16.Append(f)), decoded);
    }

    [Fact]
    public void Seven_ones_abort_the_frame()
    {
        var frame = Fcs16.Append(ModemTestSignals.UiFrame("abort me"));
        var bits = HdlcEncoder.EncodeBits([frame[..^2]], 2, 1);
        // Replace bits in the middle of the frame with an abort sequence.
        for (var i = 0; i < 7; i++)
            bits[60 + i] = true;

        Assert.Empty(DecodeBits(bits));
    }

    [Fact]
    public void Too_short_frames_are_dropped_and_the_flag_preamble_sets_dcd()
    {
        var decoder = new HdlcDecoder();
        var bits = HdlcEncoder.EncodeBits([new byte[5]], 3, 1);
        var frames = new List<byte[]>();
        var dcdSeen = false;
        foreach (var b in bits)
        {
            if (decoder.PushBit(b) is { } f)
                frames.Add(f);
            dcdSeen |= decoder.Dcd;
        }

        Assert.Empty(frames);
        Assert.True(dcdSeen);
    }

    [Fact]
    public void Over_long_frames_are_dropped()
    {
        var huge = new byte[HdlcDecoder.MaxFrameBytes + 20];
        Array.Fill(huge, (byte)0x41);

        Assert.Empty(DecodeBits(HdlcEncoder.EncodeBits([huge], 2, 1)));
    }

    [Fact]
    public void Flags_for_milliseconds_round_up()
    {
        Assert.Equal(1, HdlcEncoder.FlagsFor(0));
        Assert.Equal(45, HdlcEncoder.FlagsFor(300));
        Assert.Equal(5, HdlcEncoder.FlagsFor(30));
    }

    [Fact]
    public void Shared_zero_preamble_flags_assert_dcd_and_decode()
    {
        var payload = ModemTestSignals.UiFrame("shared zero flags");
        // Build shared-zero preamble: 0 111111 0 111111 0 111111 0 (3 flags sharing boundary zeros).
        var bits = new List<bool> { false };
        for (var f = 0; f < 3; f++)
        {
            for (var i = 0; i < 6; i++)
                bits.Add(true);
            bits.Add(false);
        }

        // Append stuffed frame body and closing flag without extra leading flags.
        bits.AddRange(HdlcEncoder.EncodeBits([payload], preambleFlags: 0, tailFlags: 1, leadingFlags: false));

        var decoder = new HdlcDecoder();
        var frames = new List<byte[]>();
        var dcdSeen = false;
        foreach (var b in bits)
        {
            if (decoder.PushBit(b) is { } frame)
                frames.Add(frame);
            dcdSeen |= decoder.Dcd;
        }

        Assert.True(dcdSeen);
        var single = Assert.Single(frames);
        Assert.Equal(Fcs16.Append(payload), single);
    }

    private static List<byte[]> DecodeBits(IEnumerable<bool> bits)
    {
        var decoder = new HdlcDecoder();
        var frames = new List<byte[]>();
        foreach (var b in bits)
        {
            if (decoder.PushBit(b) is { } f)
                frames.Add(f);
        }

        return frames;
    }
}
