using OscarWatch.Core.Cw;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Hardware;
using OscarWatch.Tests.Hardware;

namespace OscarWatch.Tests.Cw;

public sealed class CwKeyerServiceTests
{
    [Fact]
    public void Cat_line_keys_each_element_and_ends_key_up()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.YaesuFt817, cw => cw.Wpm = 60);
        using var keyer = new CwKeyerService(rig, settings);
        var finished = new ManualResetEventSlim();
        CwKeyerFinish? finish = null;
        keyer.Finished += (_, e) => { finish = e.Finish; finished.Set(); };

        Assert.Equal(CwKeyerStartResult.Started, keyer.Send(0, CwMessage.FromText("I")));
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(CwKeyerFinish.Completed, finish);
        Assert.Equal(
            ["cwkey:dtr:off", "cwkey:dtr:on", "cwkey:dtr:off", "cwkey:dtr:on", "cwkey:dtr:off"],
            rig.Calls);
        Assert.False(keyer.IsSending);
    }

    [Fact]
    public void Stop_aborts_and_leaves_the_key_up()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.YaesuFt817, cw => cw.Wpm = 5);
        using var keyer = new CwKeyerService(rig, settings);
        CwKeyerFinish? finish = null;
        keyer.Finished += (_, e) => finish = e.Finish;

        Assert.Equal(CwKeyerStartResult.Started, keyer.Send(3, CwMessage.FromText("CQ CQ CQ")));
        Assert.Equal(3, keyer.ActiveSlot);
        Thread.Sleep(100);
        keyer.Stop();

        Assert.Equal(CwKeyerFinish.Aborted, finish);
        Assert.Equal("cwkey:dtr:off", rig.Calls[^1]);
        Assert.False(keyer.IsSending);
    }

    [Fact]
    public void Kenwood_sends_speed_then_text()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.KenwoodTs590sg, cw => cw.Wpm = 60);
        using var keyer = new CwKeyerService(rig, settings);
        var finished = new ManualResetEventSlim();
        keyer.Finished += (_, _) => finished.Set();

        Assert.Equal(CwKeyerStartResult.Started, keyer.Send(0, CwMessage.FromText("E")));
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(["cwspeed:60", "cwtext:E"], rig.Calls);
    }

    [Fact]
    public void Rejected_text_reports_rig_rejected()
    {
        var rig = new RecordingRigController { CwResult = false };
        var settings = Settings(RigType.IcomIc9700);
        using var keyer = new CwKeyerService(rig, settings);
        var finished = new ManualResetEventSlim();
        CwKeyerFinish? finish = null;
        keyer.Finished += (_, e) => { finish = e.Finish; finished.Set(); };

        keyer.Send(0, CwMessage.FromText("TEST"));
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(CwKeyerFinish.RigRejected, finish);
    }

    [Fact]
    public void Rts_on_a_flow_control_cat_port_is_refused()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.YaesuFt991a, cw => cw.KeyLine = PttLine.Rts);
        using var keyer = new CwKeyerService(rig, settings);

        Assert.Equal(CwKeyerStartResult.RtsBlocked, keyer.Send(0, CwMessage.FromText("E")));
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public void Separate_port_on_the_radio_port_is_refused()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.YaesuFt817, cw =>
        {
            cw.UseSeparatePort = true;
            cw.SeparatePort = "COM7";
        });
        settings.Current.Rig.Port = "COM7";
        using var keyer = new CwKeyerService(rig, settings);

        Assert.Equal(CwKeyerStartResult.PortConflict, keyer.Send(0, CwMessage.FromText("E")));
    }

    [Fact]
    public void Line_keying_without_rig_control_is_refused()
    {
        var rig = new RecordingRigController();
        var settings = Settings(RigType.YaesuFt817);
        settings.Current.Rig.Enabled = false;
        using var keyer = new CwKeyerService(rig, settings);

        Assert.Equal(CwKeyerStartResult.RigNotConnected, keyer.Send(0, CwMessage.FromText("E")));
    }

    private static FakeSettings Settings(RigType type, Action<CwKeyerSettings>? configure = null)
    {
        var settings = new FakeSettings();
        settings.Current.Rig.Enabled = true;
        settings.Current.Rig.Type = type;
        settings.Current.QsoLogbook.CwKeyer.Enabled = true;
        configure?.Invoke(settings.Current.QsoLogbook.CwKeyer);
        return settings;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "";
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SerializeCurrent() => "";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Load() { }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RequestSave() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SyncGridFromLatLon() { }
        public void SyncLatLonFromGrid() { }
        public void EnsureSavedStations() { }
        public void ApplyActiveStation() { }
        public void SyncActiveStationFromGroundStation() { }
    }
}
