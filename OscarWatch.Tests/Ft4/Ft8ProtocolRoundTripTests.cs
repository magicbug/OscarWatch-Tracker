using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft8ProtocolRoundTripTests
{
    private const int Rate = 12000;

    [Fact]
    public void Ft8_standard_cq_encodes_to_one_15_second_slot_and_decodes()
    {
        if (!Ft8Native.IsAvailable)
            return;

        const string message = "CQ MM9SQL IO85";
        Assert.True(
            Ft8Native.TryEncodeFt4(message, 1500f, Rate, 0f, 1f, out var pcm, out var error, Ft4ProtocolProfile.Ft8),
            error);
        Assert.NotNull(pcm);
        Assert.Equal(Ft4ProtocolProfile.Ft8.SlotSamples(Rate), pcm!.Length);

        var decoded = Ft8Native.DecodeFt4(pcm, Rate, 200f, 2800f, deep: false, apHints: null, apCentreHz: 0f, Ft4ProtocolProfile.Ft8);
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ft4_encode_still_uses_the_ft4_slot()
    {
        if (!Ft8Native.IsAvailable)
            return;

        Assert.True(
            Ft8Native.TryEncodeFt4("CQ MM9SQL IO85", 1500f, Rate, out var pcm, out var error),
            error);
        Assert.Equal(Ft4ProtocolProfile.Ft4.SlotSamples(Rate), pcm!.Length);
    }
}
