using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SlotGateTests
{
    private static SatelliteTransponderMode Mode(string type) => new() { Type = type };

    [Fact]
    public void Open_window_does_not_hold_another_transponder()
    {
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("SSB Transponder")));
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: true, null));
    }

    [Fact]
    public void Ft4_transponder_holds_while_session_running()
    {
        Assert.True(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("FT4")));
        Assert.True(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode(" ft4 ")));
        Assert.True(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("FT4 Asia")));
    }

    [Fact]
    public void IsFt4_transponder_includes_custom_type_prefix()
    {
        Assert.True(Ft4SlotGate.IsFt4Transponder(Mode("FT4 Asia")));
        Assert.True(Ft4SlotGate.IsFt4Transponder(Mode("FT4-Asia")));
        Assert.False(Ft4SlotGate.IsFt4Transponder(Mode("SSB Transponder")));
        Assert.False(Ft4SlotGate.IsFt4Transponder(Mode("FT40")));
        Assert.False(Ft4SlotGate.IsFt4Transponder(Mode("Asia FT4")));
    }

    [Fact]
    public void Other_transponder_releases()
    {
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("SSB Transponder")));
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: true, Mode("CW TLM")));
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: true, null));
    }

    [Fact]
    public void No_session_never_holds()
    {
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: false, Mode("FT4")));
        Assert.False(Ft4SlotGate.ShouldHold(sessionRunning: false, Mode("FT4 Asia")));
    }
}
