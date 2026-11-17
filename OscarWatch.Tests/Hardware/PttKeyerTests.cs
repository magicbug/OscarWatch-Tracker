using OscarWatch.Core.Hardware;
using OscarWatch.Hardware;

namespace OscarWatch.Tests.Hardware;

public sealed class PttKeyerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-ptt-" + Guid.NewGuid().ToString("N"));

    public PttKeyerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PttSessionMarker Marker() => PttSessionMarker.ForTerminal(Path.Combine(_dir, "settings.json"));

    private static PttOptions Options(PttMethod method, PttLine line = PttLine.Rts, bool invert = false) =>
        new(method, line, invert, "", 0, 0);

    [Fact]
    public async Task Cat_keying_writes_the_marker_until_unkeyed()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Cat), "Terminal", marker: marker);

        await keyer.KeyAsync();
        Assert.True(keyer.IsKeyed);
        Assert.True(marker.Exists);
        Assert.Equal(PttMethod.Cat, marker.RecordedMethod);

        await keyer.UnkeyAsync();
        Assert.False(keyer.IsKeyed);
        Assert.False(marker.Exists);
        Assert.Equal(["ptt:on", "ptt:off"], rig.Calls);
    }

    [Fact]
    public async Task Vox_sends_nothing_and_leaves_no_marker()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Vox), "Terminal", marker: marker);

        await keyer.KeyAsync();
        Assert.False(marker.Exists);
        await keyer.UnkeyAsync();
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task Inverted_handshake_line_asserts_low()
    {
        var rig = new RecordingRigController();
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.CatPortHandshake, PttLine.Dtr, invert: true), "FT4");

        await keyer.KeyAsync();
        keyer.UnkeyNow();
        Assert.Equal(["dtr:off", "dtr:on"], rig.Calls);
    }

    [Fact]
    public void Force_off_unkeys_cat_even_when_not_keyed()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.Cat);
        using var keyer = new PttKeyer(rig, () => Options(PttMethod.Cat), "Terminal", marker: marker);

        keyer.ForceOff();

        Assert.Equal(["ptt:off"], rig.Calls);
        Assert.False(marker.Exists);
    }

    [Fact]
    public void Separate_port_conflict_stops_keying()
    {
        var rig = new RecordingRigController();
        var options = new PttOptions(PttMethod.SeparateComPort, PttLine.Rts, false, "COM3", 0, 0);
        using var keyer = new PttKeyer(rig, () => options, "Terminal", _ => "in use");

        var ex = Assert.Throws<InvalidOperationException>(keyer.KeyNow);
        Assert.Equal("in use", ex.Message);
        Assert.False(keyer.IsKeyed);
    }

    [Fact]
    public async Task Crash_recovery_unkeys_cat_once_the_rig_connects()
    {
        var rig = new RecordingRigController { Connected = false };
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.Cat);
        using var recovery = new PttCrashRecovery(rig, marker);
        var recovered = new TaskCompletionSource();
        recovery.Recovered += (_, _) => recovered.TrySetResult();

        recovery.Start();
        await Task.Delay(300);
        Assert.Empty(rig.Calls);

        rig.Connected = true;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["ptt:off"], rig.Calls);
        Assert.False(marker.Exists);
        Assert.True(recovery.HasNotice);
    }

    [Fact]
    public void Crash_recovery_ignores_methods_that_drop_on_their_own()
    {
        var rig = new RecordingRigController();
        var marker = Marker();
        marker.MarkKeyed("Terminal", PttMethod.SeparateComPort);
        using var recovery = new PttCrashRecovery(rig, marker);

        recovery.Start();

        Assert.Empty(rig.Calls);
        Assert.False(marker.Exists);
        Assert.False(recovery.HasNotice);
    }

    [Fact]
    public void ReleaseUnusedPort_drops_keyed_line_even_when_another_lease_keeps_port_open()
    {
        FakeHandshakePort? created = null;
        var pool = new HandshakePortPool(name => created = new FakeHandshakePort(name));
        var rig = new RecordingRigController();
        var options = new PttOptions(PttMethod.SeparateComPort, PttLine.Rts, false, "COM8", 0, 0);
        using var cwLease = pool.Acquire("COM8", "CW keyer");
        using var keyer = new PttKeyer(rig, () => options, "FT4", portPool: pool);

        keyer.KeyNow();
        Assert.True(keyer.IsKeyed);
        Assert.True(created!.RtsEnable);

        options = new PttOptions(PttMethod.Cat, PttLine.Rts, false, "COM8", 0, 0);
        keyer.ReleaseUnusedPort();

        Assert.False(keyer.IsKeyed);
        Assert.False(created.RtsEnable);
        Assert.True(created.IsOpen);
    }

    private sealed class FakeHandshakePort(string portName) : IHandshakeSerialPort
    {
        public string PortName { get; } = portName;
        public bool IsOpen { get; private set; }
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public void Open() => IsOpen = true;
        public void Dispose() => IsOpen = false;
    }
}
