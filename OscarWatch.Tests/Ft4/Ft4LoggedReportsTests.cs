using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4LoggedReportsTests
{
    [Fact]
    public void Sequencer_reports_are_kept()
    {
        var (sent, received) = Ft4LoggedReports.Resolve(
            "MM9SQL",
            "G4ABC",
            "-08",
            "R+12",
            "G4ABC MM9SQL 73",
            [Line("G4ABC MM9SQL +01", transmitted: true), Line("MM9SQL G4ABC -20")]);

        Assert.Equal("-08", sent);
        Assert.Equal("+12", received);
    }

    [Fact]
    public void Missing_reports_come_from_the_messages_on_the_list()
    {
        Ft4DecodedMessage[] newestFirst =
        [
            Line("G4ABC MM9SQL 73", transmitted: true),
            Line("MM9SQL G4ABC RR73"),
            Line("G4ABC MM9SQL R-08", transmitted: true),
            Line("MM9SQL G4ABC +05"),
            Line("CQ MM9SQL IO85", transmitted: true),
            Line("G4ABC MM9SQL +19", transmitted: true),
            Line("MM9SQL G4ABC -15")
        ];

        var (sent, received) = Ft4LoggedReports.Resolve(
            "MM9SQL",
            "G4ABC",
            reportSent: null,
            reportReceived: null,
            pendingTxMessage: "CQ MM9SQL IO85",
            newestFirst);

        Assert.Equal("-08", sent);
        Assert.Equal("+05", received);
    }

    [Fact]
    public void A_cq_from_either_station_hides_the_previous_contact()
    {
        var (sent, received) = Ft4LoggedReports.Resolve(
            "MM9SQL",
            "G4ABC",
            reportSent: null,
            reportReceived: null,
            pendingTxMessage: "G4ABC MM9SQL IO85",
            [
                Line("CQ G4ABC JO01"),
                Line("G4ABC MM9SQL -08", transmitted: true),
                Line("MM9SQL G4ABC +05")
            ]);

        Assert.Equal("", sent);
        Assert.Equal("", received);
    }

    [Fact]
    public void The_tx_message_fills_a_report_that_has_not_gone_out()
    {
        var (sent, received) = Ft4LoggedReports.Resolve(
            "MM9SQL",
            "G4ABC",
            reportSent: null,
            reportReceived: "+05",
            pendingTxMessage: "G4ABC MM9SQL R-11",
            [Line("MM9SQL G4ABC +05")]);

        Assert.Equal("-11", sent);
        Assert.Equal("+05", received);
    }

    [Fact]
    public void A_rejected_hint_is_not_logged()
    {
        var (sent, received) = Ft4LoggedReports.Resolve(
            "MM9SQL",
            "G4ABC",
            reportSent: "-08",
            reportReceived: null,
            pendingTxMessage: "G4ABC MM9SQL 73",
            [Line("MM9SQL G4ABC +21", rejected: true), Line("MM9SQL G4ABC -03")]);

        Assert.Equal("-08", sent);
        Assert.Equal("-03", received);
    }

    [Fact]
    public void Our_own_echo_counts_as_the_report_we_sent()
    {
        var (sent, received) = Ft4LoggedReports.Resolve(
            "mm9sql",
            "g4abc",
            reportSent: null,
            reportReceived: null,
            pendingTxMessage: "",
            [Line("G4ABC MM9SQL -04", ownEcho: true), Line("MM9SQL G4ABC R+02")]);

        Assert.Equal("-04", sent);
        Assert.Equal("+02", received);
    }

    private static Ft4DecodedMessage Line(
        string text,
        bool transmitted = false,
        bool ownEcho = false,
        bool rejected = false) =>
        new(DateTime.UtcNow, text, 1500f, 0.2f, -8f, null, null, null, ownEcho, transmitted, IsRejected: rejected);
}
