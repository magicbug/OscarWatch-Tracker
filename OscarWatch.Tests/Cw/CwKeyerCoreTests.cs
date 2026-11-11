using System.Text;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests.Cw;

public sealed class CwKeyerCoreTests
{
    private static readonly CwMacroContext Context = new(
        MyCall: "MM9SQL",
        Call: "G4ABC",
        RstSent: "599",
        RstRcvd: "579",
        Grid: "IO91",
        MyGrid: "IO87",
        Name: "Bob",
        Satellite: "RS-44");

    [Theory]
    [InlineData("CQ SAT DE {MYCALL} {MYCALL}", "CQ SAT DE MM9SQL MM9SQL")]
    [InlineData("{CALL} {SENTRST} {MYGRID}", "G4ABC 599 IO87")]
    [InlineData("! {SENTRSTCUT}", "G4ABC 5NN")]
    [InlineData("cq de *", "CQ DE MM9SQL")]
    [InlineData("{rcvdrst} {grid} {name} {sat}", "579 IO91 BOB RS-44")]
    [InlineData("TU {MYCALL} {SK}", "TU MM9SQL <SK>")]
    [InlineData("{AR} {BT} {KN}", "+ = <KN>")]
    [InlineData("  CQ    CQ  ", "CQ CQ")]
    public void Macros_expand_like_n1mm(string template, string expected)
    {
        var expansion = CwMacroExpander.Expand(template, Context);

        Assert.Equal(expected, expansion.Message.DisplayText);
        Assert.Empty(expansion.UnknownTokens);
    }

    [Fact]
    public void Unknown_tokens_are_not_sent_and_are_reported()
    {
        var expansion = CwMacroExpander.Expand("{CALL} {EXCH} TU {exch}", Context);

        Assert.Equal("G4ABC TU", expansion.Message.DisplayText);
        Assert.Equal(["EXCH"], expansion.UnknownTokens);
    }

    [Fact]
    public void Empty_fields_leave_no_stray_gaps()
    {
        var expansion = CwMacroExpander.Expand("{CALL} {SENTRST}", new CwMacroContext(RstSent: "599"));

        Assert.Equal("599", expansion.Message.DisplayText);
    }

    [Fact]
    public void Unsupported_characters_are_dropped()
    {
        var expansion = CwMacroExpander.Expand("CQ # DE ~ {MYCALL}", Context);

        Assert.Equal("CQ DE MM9SQL", expansion.Message.DisplayText);
    }

    [Fact]
    public void Key_events_follow_paris_timing()
    {
        var events = MorseCode.BuildKeyEvents(CwMessage.FromText("ET E"));

        // E . | char gap | T - | word gap | E .
        Assert.Equal(
            [
                new CwKeyEvent(true, 1),
                new CwKeyEvent(false, 3),
                new CwKeyEvent(true, 3),
                new CwKeyEvent(false, 7),
                new CwKeyEvent(true, 1)
            ],
            events);
    }

    [Fact]
    public void Paris_is_fifty_units_with_its_word_gap()
    {
        var units = MorseCode.CountUnits(CwMessage.FromText("PARIS")) + MorseCode.WordGapUnits;

        Assert.Equal(50, units);
        Assert.Equal(60.0, MorseCode.DotMs(20), 3);
    }

    [Fact]
    public void Prosign_letters_run_together_without_a_character_gap()
    {
        var message = CwMacroExpander.Expand("{SK}", Context).Message;
        var events = MorseCode.BuildKeyEvents(message);

        // ...-.- : six elements, five one-unit gaps, no three-unit gap.
        Assert.Equal(11, events.Count);
        Assert.DoesNotContain(events, e => !e.KeyDown && e.Units > 1);
    }

    [Fact]
    public void Kenwood_ky_pads_to_24_characters_and_maps_prosigns()
    {
        var message = CwMacroExpander.Expand("TU {SK}", Context).Message;
        var commands = KenwoodCwCodec.BuildSendCommands(message);

        var command = Assert.Single(commands).Command;
        Assert.Equal("KY " + "TU >".PadRight(24) + ";", command);
        Assert.Equal(28, command.Length);
    }

    [Fact]
    public void Kenwood_long_messages_split_between_words_with_the_gap_leading()
    {
        var message = CwMessage.FromText("CQ SAT CQ SAT DE MM9SQL MM9SQL K");
        var commands = KenwoodCwCodec.BuildSendCommands(message);

        Assert.Equal(2, commands.Count);
        Assert.Equal("KY " + "CQ SAT CQ SAT DE MM9SQL".PadRight(24) + ";", commands[0].Command);
        Assert.Equal("KY " + " MM9SQL K".PadRight(24) + ";", commands[1].Command);
    }

    [Theory]
    [InlineData(20, false, "KS020;")]
    [InlineData(2, false, "KS004;")]
    [InlineData(5, true, "KS010;")]
    [InlineData(70, true, "KS060;")]
    public void Kenwood_speed_is_clamped_per_radio(int wpm, bool ts2000, string expected) =>
        Assert.Equal(expected, KenwoodCwCodec.BuildSpeedCommand(wpm, ts2000));

    [Fact]
    public void Kenwood_stop_differs_on_the_ts2000()
    {
        Assert.Equal("KY0;", KenwoodCwCodec.BuildStopCommand(ts2000: false));
        Assert.Equal("KY " + new string(' ', 24) + ";", KenwoodCwCodec.BuildStopCommand(ts2000: true));
    }

    [Fact]
    public void Icom_text_is_command_17_with_ascii_and_caret_prosigns()
    {
        var message = CwMacroExpander.Expand("TU {SK} {AR}", Context).Message;
        var body = Assert.Single(IcomCwCodec.BuildSendCommands(message)).Body;

        Assert.Equal(0x17, body[0]);
        Assert.Equal("TU ^SK +", Encoding.ASCII.GetString(body, 1, body.Length - 1));
    }

    [Fact]
    public void Icom_pieces_stay_within_30_characters_and_keep_prosigns_whole()
    {
        var message = CwMacroExpander.Expand("CQ SAT CQ SAT DE {MYCALL} {MYCALL} {MYCALL} {KN}", Context).Message;
        var commands = IcomCwCodec.BuildSendCommands(message);

        Assert.All(commands, c => Assert.True(c.Body.Length - 1 <= IcomCwCodec.MaxCharacters));
        var joined = string.Concat(commands.Select(c => Encoding.ASCII.GetString(c.Body, 1, c.Body.Length - 1)));
        Assert.Equal("CQ SAT CQ SAT DE MM9SQL MM9SQL MM9SQL ^KN", joined);
    }

    [Fact]
    public void Icom_stop_and_speed()
    {
        Assert.Equal(new byte[] { 0x17, 0xFF }, IcomCwCodec.BuildStopCommand());
        Assert.Equal(new byte[] { 0x14, 0x0C, 0x00, 0x00 }, IcomCwCodec.BuildSpeedCommand(6));
        Assert.Equal(new byte[] { 0x14, 0x0C, 0x02, 0x55 }, IcomCwCodec.BuildSpeedCommand(48));
        Assert.Equal(new byte[] { 0x14, 0x0C, 0x00, 0x85 }, IcomCwCodec.BuildSpeedCommand(20));
        Assert.Equal(0, IcomCwCodec.SpeedLevel(2));
        Assert.Equal(255, IcomCwCodec.SpeedLevel(60));
    }

    [Fact]
    public void Chunker_hard_splits_a_word_longer_than_a_piece()
    {
        var pieces = CwTextChunker.Split(CwMessage.FromText("ABCDEFGH"), 3, _ => 1);

        Assert.Equal(["ABC", "DEF", "GH"], pieces.Select(p => p.DisplayText));
    }

    [Theory]
    [InlineData(RigType.KenwoodTs590sg, false, false, CwKeyingMethod.KenwoodText)]
    [InlineData(RigType.KenwoodTs2000, true, true, CwKeyingMethod.KenwoodText)]
    [InlineData(RigType.IcomIc9700, false, false, CwKeyingMethod.IcomText)]
    [InlineData(RigType.IcomIc9700, true, true, CwKeyingMethod.SeparatePortLine)]
    [InlineData(RigType.IcomIc7300, true, false, CwKeyingMethod.CatPortLine)]
    [InlineData(RigType.IcomIc910, false, false, CwKeyingMethod.CatPortLine)]
    [InlineData(RigType.YaesuFt991a, false, true, CwKeyingMethod.SeparatePortLine)]
    public void Route_follows_the_uplink_radio(RigType type, bool comOverride, bool separate, CwKeyingMethod expected)
    {
        var cw = new CwKeyerSettings { UseComLineOnTextRadios = comOverride, UseSeparatePort = separate };
        var rig = new RigSettings { Enabled = true, Type = type };

        Assert.Equal(expected, CwKeyingRoute.Resolve(cw, rig));
    }

    [Fact]
    public void Route_uses_the_uplink_leg_on_dual_radio()
    {
        var rig = new RigSettings { Enabled = true, DualRadioEnabled = true, Type = RigType.IcomIc9700 };
        rig.Uplink.Type = RigType.KenwoodTs890s;

        Assert.Equal(CwKeyingMethod.KenwoodText, CwKeyingRoute.Resolve(new CwKeyerSettings(), rig));
    }

    [Fact]
    public void Rts_on_a_yaesu_cat_port_is_blocked()
    {
        var rig = new RigSettings { Enabled = true, Type = RigType.YaesuFt991 };

        Assert.True(CwKeyingRoute.CatPortRtsBlocked(new CwKeyerSettings { KeyLine = PttLine.Rts }, rig));
        Assert.False(CwKeyingRoute.CatPortRtsBlocked(new CwKeyerSettings { KeyLine = PttLine.Dtr }, rig));
        Assert.False(CwKeyingRoute.CatPortRtsBlocked(
            new CwKeyerSettings { KeyLine = PttLine.Rts, UseSeparatePort = true }, rig));
    }

    [Fact]
    public void Settings_normalise_to_twelve_slots_and_a_valid_speed()
    {
        var cw = new CwKeyerSettings
        {
            Wpm = 200,
            Messages = [new CwKeyerMessage { Label = "  A very long button name  ", Text = " CQ " }]
        };

        cw.Normalise();

        Assert.Equal(CwKeyerSettings.MaxWpm, cw.Wpm);
        Assert.Equal(CwKeyerSettings.MessageCount, cw.Messages.Count);
        Assert.Equal("A very long", cw.Messages[0].Label);
        Assert.Equal("CQ", cw.Messages[0].Text);
    }

    [Fact]
    public void Default_messages_match_the_operator_guide()
    {
        var messages = CwKeyerSettings.CreateDefaultMessages();

        Assert.Equal(12, messages.Count);
        Assert.Equal("CQ SAT DE {MYCALL} {MYCALL}", messages[0].Text);
        Assert.Equal("TU {MYCALL} SK", messages[5].Text);
        Assert.All(messages.Skip(6), m => Assert.Equal("", m.Text));
    }
}
