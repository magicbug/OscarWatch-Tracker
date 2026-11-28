using OscarWatch.Core.Hardware;
using OscarWatch.Hardware;

namespace OscarWatch.Tests.Hardware;

public sealed class PttTestSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-ptt-test-" + Guid.NewGuid().ToString("N"));

    public PttTestSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PttSessionMarker Marker() => PttSessionMarker.ForTerminal(Path.Combine(_dir, "settings.json"));

    private static PttOptions Options(
        PttMethod method,
        PttLine line = PttLine.Rts,
        bool invert = false,
        string port = "",
        int leadMs = 0,
        int tailMs = 0) =>
        new(method, line, invert, port, leadMs, tailMs);

    [Fact]
    public async Task Cat_keys_then_unkeys_after_the_hold()
    {
        var rig = new RecordingRigController();
        using var session = new PttTestSession(rig, Options(PttMethod.Cat), marker: Marker());

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        Assert.Equal(["ptt:on"], rig.Calls);

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PttTestOutcome.Completed, outcome);
        Assert.False(session.IsKeyed);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
        Assert.False(Marker().Exists);
    }

    [Fact]
    public async Task Cancel_unkeys_before_the_hold_ends()
    {
        var rig = new RecordingRigController();
        using var session = new PttTestSession(rig, Options(PttMethod.Cat), marker: Marker());

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        session.Cancel();

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PttTestOutcome.Cancelled, outcome);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
    }

    [Fact]
    public async Task Dispose_while_keyed_unkeys()
    {
        var rig = new RecordingRigController();
        var session = new PttTestSession(rig, Options(PttMethod.Cat), marker: Marker());

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        session.Dispose();

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PttTestOutcome.Cancelled, outcome);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
        Assert.False(Marker().Exists);
    }

    [Fact]
    public async Task Handshake_rts_asserts_high_then_low()
    {
        var rig = new RecordingRigController();
        using var session = new PttTestSession(rig, Options(PttMethod.CatPortHandshake, PttLine.Rts));

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        session.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["rts:on", "rts:off"], rig.Calls);
    }

    [Fact]
    public async Task Inverted_handshake_dtr_asserts_low_then_high()
    {
        var rig = new RecordingRigController();
        using var session = new PttTestSession(
            rig,
            Options(PttMethod.CatPortHandshake, PttLine.Dtr, invert: true));

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        session.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["dtr:off", "dtr:on"], rig.Calls);
    }

    [Fact]
    public async Task Rig_disconnect_during_cat_test_force_offs()
    {
        var rig = new RecordingRigController();
        using var session = new PttTestSession(rig, Options(PttMethod.Cat), marker: Marker());

        var run = session.RunAsync();
        await WaitUntil(() => session.IsKeyed);
        rig.Connected = false;

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PttTestOutcome.RigDisconnected, outcome);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
    }

    [Fact]
    public void Block_reason_requires_connected_rig_for_cat()
    {
        Assert.Equal(
            PttTestBlockReason.NoRig,
            PttTestSession.GetBlockReason(
                PttMethod.Cat,
                hasRig: false,
                rigConnected: false,
                terminalTransmitting: false,
                ft4Active: false,
                cwSending: false,
                testToneBusy: false,
                separatePortConflict: false,
                sessionActive: false));

        Assert.Equal(
            PttTestBlockReason.RigDisconnected,
            PttTestSession.GetBlockReason(
                PttMethod.Cat,
                hasRig: true,
                rigConnected: false,
                terminalTransmitting: false,
                ft4Active: false,
                cwSending: false,
                testToneBusy: false,
                separatePortConflict: false,
                sessionActive: false));

        Assert.Equal(
            PttTestBlockReason.None,
            PttTestSession.GetBlockReason(
                PttMethod.Cat,
                hasRig: true,
                rigConnected: true,
                terminalTransmitting: false,
                ft4Active: false,
                cwSending: false,
                testToneBusy: false,
                separatePortConflict: false,
                sessionActive: false));
    }

    [Fact]
    public void Block_reason_allows_vox_without_a_rig()
    {
        Assert.Equal(
            PttTestBlockReason.None,
            PttTestSession.GetBlockReason(
                PttMethod.Vox,
                hasRig: false,
                rigConnected: false,
                terminalTransmitting: false,
                ft4Active: false,
                cwSending: false,
                testToneBusy: false,
                separatePortConflict: false,
                sessionActive: false));
    }

    [Theory]
    [InlineData(true, false, false, PttTestBlockReason.TerminalBusy)]
    [InlineData(false, true, false, PttTestBlockReason.Ft4Busy)]
    [InlineData(false, false, true, PttTestBlockReason.CwBusy)]
    public void Block_reason_stops_while_another_feature_is_transmitting(
        bool terminal,
        bool ft4,
        bool cw,
        PttTestBlockReason expected)
    {
        Assert.Equal(
            expected,
            PttTestSession.GetBlockReason(
                PttMethod.Cat,
                hasRig: true,
                rigConnected: true,
                terminalTransmitting: terminal,
                ft4Active: ft4,
                cwSending: cw,
                testToneBusy: false,
                separatePortConflict: false,
                sessionActive: false));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
