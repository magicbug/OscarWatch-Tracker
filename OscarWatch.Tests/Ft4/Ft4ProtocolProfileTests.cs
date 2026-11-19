using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4ProtocolProfileTests
{
    private const int Rate = 12000;

    private static SatelliteTransponderMode Mode(string type) => new() { Type = type };

    [Fact]
    public void Ft8_transponder_selects_the_ft8_profile()
    {
        var profile = Ft4ProtocolProfile.For(Mode("FT8"));

        Assert.Equal("FT8", profile.Mode);
        Assert.False(profile.IsFt4);
        Assert.Equal(15.0, profile.SlotSeconds);
        Assert.Equal(12.64, profile.BurstSeconds, 3);
    }

    [Fact]
    public void Custom_ft8_row_selects_ft8_and_ft80_does_not()
    {
        Assert.Equal("FT8", Ft4ProtocolProfile.For(Mode("FT8 Asia")).Mode);
        Assert.Equal("FT8", Ft4ProtocolProfile.For(Mode(" ft8 ")).Mode);
        Assert.Equal("FT4", Ft4ProtocolProfile.For(Mode("FT80")).Mode);
    }

    [Fact]
    public void Ft4_and_missing_transponder_keep_the_ft4_timing()
    {
        Assert.Equal("FT4", Ft4ProtocolProfile.For(Mode("FT4")).Mode);
        Assert.Equal("FT4", Ft4ProtocolProfile.For(null).Mode);
        Assert.Equal(Ft4SlotClock.Ft4SlotSeconds, Ft4ProtocolProfile.Ft4.SlotSeconds);
        Assert.Equal(Ft4SlotClock.PreTransmitCatLead, Ft4ProtocolProfile.Ft4.PreTransmitCatLead);
    }

    [Fact]
    public void Gate_recognises_ft8_as_a_modem_transponder_without_changing_ft4_only_checks()
    {
        Assert.True(Ft4SlotGate.IsFt8Transponder(Mode("FT8")));
        Assert.True(Ft4SlotGate.IsFt8Transponder(Mode("FT8-Asia")));
        Assert.False(Ft4SlotGate.IsFt8Transponder(Mode("FT80")));
        Assert.False(Ft4SlotGate.IsFt8Transponder(Mode("FT4")));
        Assert.False(Ft4SlotGate.IsFt8Transponder(null));

        Assert.True(Ft4SlotGate.IsModemTransponder(Mode("FT4")));
        Assert.True(Ft4SlotGate.IsModemTransponder(Mode("FT8")));
        Assert.False(Ft4SlotGate.IsModemTransponder(Mode("SSB Transponder")));

        Assert.False(Ft4SlotGate.IsFt4Transponder(Mode("FT8")));
    }

    [Fact]
    public void Ft8_dial_is_held_while_the_session_runs()
    {
        Assert.True(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("FT8")));
        Assert.True(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("FT8 Asia")));
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: false, Mode("FT8")));
    }

    [Fact]
    public void Ft8_cat_lead_starts_after_the_receive_burst()
    {
        var profile = Ft4ProtocolProfile.Ft8;
        var burstEnd = 0.5 + profile.BurstSeconds;

        Assert.True(profile.SlotSeconds - profile.PreTransmitCatLead.TotalSeconds > burstEnd + 0.1);
    }

    [Fact]
    public void Ft8_early_decode_waits_for_the_burst_and_stays_inside_the_slot()
    {
        var profile = Ft4ProtocolProfile.Ft8;

        Assert.True(profile.EarlyDecodeSeconds > 0.5 + profile.BurstSeconds);
        Assert.True(profile.EarlyDecodeSeconds < profile.SlotSeconds);
    }

    [Fact]
    public void Encode_capacity_holds_one_full_slot()
    {
        Assert.True(Ft4ProtocolProfile.Ft8.EncodeCapacity(Rate) >= Ft4ProtocolProfile.Ft8.SlotSamples(Rate));
        Assert.True(Ft4ProtocolProfile.Ft4.EncodeCapacity(Rate) >= Ft4ProtocolProfile.Ft4.SlotSamples(Rate));
    }

    [Fact]
    public void Half_slot_thresholds_follow_the_profile()
    {
        Assert.Equal(45_000, Ft4ProtocolProfile.Ft4.HalfSlotSamples(Rate));
        Assert.Equal(90_000, Ft4ProtocolProfile.Ft8.HalfSlotSamples(Rate));
    }
}
