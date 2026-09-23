using OscarWatch.Core.Ft4;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4RxMarkerTests
{
    private static Ft4DecodedMessage Rx(string text, string de, float hz) =>
        new(DateTime.UtcNow, text, hz, 0.2f, -10f, "MM9SQL", de, "IO91", false);

    [Fact]
    public void Partner_frequency_wins_over_a_newer_unrelated_decode()
    {
        var decodes = new[]
        {
            Rx("M0ABC G4ZZZ IO91", "G4ZZZ", 2100f),
            Rx("MM9SQL G4ABC IO91", "G4ABC", 1420f),
        };

        var found = Ft4ViewModel.TryPartnerRxHz(decodes, "G4ABC", out var hz);

        Assert.True(found);
        Assert.Equal(1420, hz);
    }

    [Fact]
    public void Own_echo_and_our_tx_do_not_move_the_partner_marker()
    {
        var echo = new Ft4DecodedMessage(
            DateTime.UtcNow, "G4ABC MM9SQL R-10", 1500f, 0.1f, 5f, "G4ABC", "MM9SQL", "R-10",
            IsOwnEcho: true);
        var ours = new Ft4DecodedMessage(
            DateTime.UtcNow, "G4ABC MM9SQL R-10", 1500f, 0.1f, 0f, "G4ABC", "MM9SQL", "R-10",
            IsOwnEcho: false, IsTransmitted: true);
        var them = Rx("MM9SQL G4ABC R-05", "G4ABC", 1380f);

        var found = Ft4ViewModel.TryPartnerRxHz([echo, ours, them], "G4ABC", out var hz);

        Assert.True(found);
        Assert.Equal(1380, hz);
    }

    [Fact]
    public void Missing_partner_leaves_the_marker_alone()
    {
        var found = Ft4ViewModel.TryPartnerRxHz(
            [Rx("M0ABC G4ZZZ IO91", "G4ZZZ", 2100f)],
            "G4ABC",
            out _);

        Assert.False(found);
    }
}
