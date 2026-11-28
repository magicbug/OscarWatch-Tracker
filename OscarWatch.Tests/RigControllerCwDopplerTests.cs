using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public class RigControllerCwDopplerTests
{
    [Fact]
    public void Timed_out_cw_text_is_not_delivered_after_a_blocked_doppler_write()
    {
        var rig = new BlockingCwDriver();
        var controller = new RigController(_ => rig);
        try
        {
            controller.Update(FmSettings(), FmContext());
            controller.SetFt4SlotGatedDoppler(true);
            controller.DrainCommandQueueForTests();

            rig.ArmBlock = true;
            controller.ForceFt4DopplerStep();
            Assert.True(rig.Entered.Wait(TimeSpan.FromSeconds(3)), "Doppler step did not reach a frequency write");

            Assert.False(controller.TrySendCwText(CwMessage.FromText("CQ"), TimeSpan.FromMilliseconds(200)));

            rig.Release.Set();
            controller.DrainCommandQueueForTests();

            Assert.True(controller.TryStopCwText());
            controller.DrainCommandQueueForTests();

            var events = rig.Snapshot();
            Assert.Contains("freq", events);
            Assert.DoesNotContain(events, e => e.StartsWith("cwtext:", StringComparison.Ordinal));
            Assert.Contains("cwstop", events);
        }
        finally
        {
            rig.Release.Set();
            controller.Dispose();
            rig.Entered.Dispose();
            rig.Release.Dispose();
        }
    }

    [Fact]
    public void Cw_text_doppler_hold_skips_frequency_writes_until_cleared()
    {
        var rig = new BlockingCwDriver();
        var controller = new RigController(_ => rig);
        try
        {
            controller.Update(FmSettings(), FmContext());
            controller.SetFt4SlotGatedDoppler(true);
            controller.SetCwTextDopplerHold(true);
            controller.DrainCommandQueueForTests();
            var afterConnect = rig.Snapshot().Count(e => e == "freq");

            controller.ForceFt4DopplerStep();
            controller.DrainCommandQueueForTests();
            Assert.Equal(afterConnect, rig.Snapshot().Count(e => e == "freq"));

            controller.SetCwTextDopplerHold(false);
            controller.DrainCommandQueueForTests();
            controller.ForceFt4DopplerStep();
            controller.DrainCommandQueueForTests();

            Assert.True(rig.Snapshot().Count(e => e == "freq") > afterConnect);
        }
        finally
        {
            controller.Dispose();
            rig.Entered.Dispose();
            rig.Release.Dispose();
        }
    }

    [Fact]
    public void Disconnect_still_runs_when_its_wait_times_out()
    {
        var rig = new BlockingDisposeDriver();
        var controller = new RigController(_ => rig);
        try
        {
            controller.Update(FmSettings(), FmContext());
            controller.DrainCommandQueueForTests();
            Assert.True(rig.IsConnected);

            rig.ArmDisposeBlock = true;
            var timedOut = false;
            var waiter = new Thread(() =>
            {
                try
                {
                    controller.DisconnectAndWaitForTests(TimeSpan.FromMilliseconds(200));
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }
            })
            {
                IsBackground = true
            };
            waiter.Start();

            Assert.True(rig.DisposeEntered.Wait(TimeSpan.FromSeconds(3)), "Disconnect did not reach Dispose");
            Assert.True(waiter.Join(TimeSpan.FromSeconds(3)));
            Assert.True(timedOut, "Disconnect wait should have timed out while Dispose was blocked");

            rig.DisposeRelease.Set();
            controller.DrainCommandQueueForTests();
            Assert.False(rig.IsConnected);
            Assert.True(rig.Disposed);
        }
        finally
        {
            rig.DisposeRelease.Set();
            controller.Dispose();
            rig.DisposeEntered.Dispose();
            rig.DisposeRelease.Dispose();
        }
    }

    private static RigSettings FmSettings() => new()
    {
        Enabled = true,
        Type = RigType.IcomIc9700,
        Port = "COM4",
        DopplerThresholdFmHz = 10,
        CatDelayMs = 0,
        DopplerCatLeadEnabled = false
    };

    private static RigTrackingContext FmContext() => new()
    {
        TrackState = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 1.2)
        },
        Mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 435_640,
            UplinkKHz = 145_965,
            DownlinkMode = "FMN",
            UplinkMode = "FMN"
        },
        Corrected = new CorrectedFrequencies(145_965, 435_640, 145_965, 435_640, 0, false)
    };

    private sealed class BlockingCwDriver : IRigDriver
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool ArmBlock { get; set; }

        public bool IsConnected => true;
        public RigType RigType => RigType.IcomIc9700;
        public bool SupportsTracking => true;
        public bool SupportsCatPtt => true;
        public bool SupportsCwText => true;

        public void Open() { }

        public long? ReadFrequencyHz(RigVfo vfo) => vfo is RigVfo.Sub or RigVfo.VfoB ? 145_965_000 : 435_640_000;

        public bool SetFrequencyHz(long hz)
        {
            if (ArmBlock)
            {
                ArmBlock = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }

            lock (_gate)
                _events.Add("freq");
            return true;
        }

        public bool TrySendCwText(CwMessage piece)
        {
            lock (_gate)
                _events.Add("cwtext:" + piece.DisplayText);
            return true;
        }

        public bool TryStopCwText()
        {
            lock (_gate)
                _events.Add("cwstop");
            return true;
        }

        public bool TrySetCwSpeed(int wpm)
        {
            lock (_gate)
                _events.Add($"cwspeed:{wpm}");
            return true;
        }

        public void SetPtt(bool transmit)
        {
            lock (_gate)
                _events.Add(transmit ? "ptt-on" : "ptt-off");
        }

        public List<string> Snapshot()
        {
            lock (_gate)
                return [.. _events];
        }

        public void SelectVfo(RigVfo vfo, bool force = false) { }
        public void SetMode(string mode) { }
        public void SetSplitOn(bool on) { }
        public void SetSatelliteMode(bool on) { }
        public void ExchangeVfos() { }
        public void SetToneOn(bool on) { }
        public void SetToneSquelchOn(bool on) { }
        public void SetToneHz(double hz, bool squelchTone) { }
        public void Dispose() { }
    }

    private sealed class BlockingDisposeDriver : IRigDriver
    {
        public ManualResetEventSlim DisposeEntered { get; } = new(false);
        public ManualResetEventSlim DisposeRelease { get; } = new(false);
        public bool ArmDisposeBlock { get; set; }
        public bool Disposed { get; private set; }

        public bool IsConnected { get; private set; }
        public RigType RigType => RigType.IcomIc9700;
        public bool SupportsTracking => true;
        public bool SupportsCatPtt => true;

        public void Open() => IsConnected = true;

        public long? ReadFrequencyHz(RigVfo vfo) => vfo is RigVfo.Sub or RigVfo.VfoB ? 145_965_000 : 435_640_000;

        public bool SetFrequencyHz(long hz) => true;

        public void SetPtt(bool transmit) { }
        public void SelectVfo(RigVfo vfo, bool force = false) { }
        public void SetMode(string mode) { }
        public void SetSplitOn(bool on) { }
        public void SetSatelliteMode(bool on) { }
        public void ExchangeVfos() { }
        public void SetToneOn(bool on) { }
        public void SetToneSquelchOn(bool on) { }
        public void SetToneHz(double hz, bool squelchTone) { }

        public void Dispose()
        {
            if (ArmDisposeBlock)
            {
                ArmDisposeBlock = false;
                DisposeEntered.Set();
                DisposeRelease.Wait(TimeSpan.FromSeconds(5));
            }

            IsConnected = false;
            Disposed = true;
        }
    }
}
