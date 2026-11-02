using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public class RigControllerModeSwitchTests
{
    [Fact]
    public void Downlink_only_mode_change_retries_uplink_and_ignores_stale_dial()
    {
        const long ft4DownlinkHz = 435_611_000;
        const long ssbDownlinkHz = 435_667_000;
        const long ssbUplinkHz = 145_937_610;

        var rig = new StaleDialRigDriver
        {
            MainHz = ft4DownlinkHz,
            SubHz = 145_993_610,
            ForcedMainReadHz = ft4DownlinkHz
        };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM1",
            CatDelayMs = 0,
            DopplerThresholdLinearHz = 50,
            InteractiveDialSettleMs = 200
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        controller.Update(settings, Context(state, Ft4Mode(), DopplerStrategy.Full));

        rig.FailSubWritesRemaining = 1;
        controller.Update(settings, Context(state, SsbMode(), DopplerStrategy.DownlinkOnly));

        Assert.Equal(ssbDownlinkHz, rig.MainHz);
        Assert.Equal(ssbUplinkHz, rig.SubHz);
        Assert.True(rig.FailedSubWrites >= 1);

        Thread.Sleep(450);
        for (var i = 0; i < 8; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.05, 0.05);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.05, 0.05);
        Assert.Equal(ssbDownlinkHz, rig.MainHz);
        Assert.Equal(ssbUplinkHz, rig.SubHz);
    }

    private static RigTrackingContext Context(
        SatelliteTrackState state,
        SatelliteTransponderMode mode,
        DopplerStrategy strategy) =>
        new()
        {
            TrackState = state,
            Mode = mode,
            DopplerStrategy = strategy,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0, 0, 0, 0, strategy)
        };

    private static SatelliteTransponderMode Ft4Mode() => new()
    {
        Type = "FT4",
        DownlinkKHz = 435_611,
        UplinkKHz = 145_993.61,
        DownlinkMode = "USB",
        UplinkMode = "LSB",
        Doppler = "REV"
    };

    private static SatelliteTransponderMode SsbMode() => new()
    {
        Type = "SSB",
        DownlinkKHz = 435_667,
        UplinkKHz = 145_937.61,
        DownlinkMode = "USB",
        UplinkMode = "LSB",
        Doppler = "REV"
    };

    private sealed class StaleDialRigDriver : IRigDriver
    {
        public long MainHz { get; set; }
        public long SubHz { get; set; }
        public long? ForcedMainReadHz { get; set; }
        public int FailSubWritesRemaining { get; set; }
        public int FailedSubWrites { get; private set; }

        public RigType RigType => RigType.IcomIc9700;
        public bool IsConnected => true;
        public bool SupportsTracking => true;

        public void Open()
        {
        }

        public long? ReadFrequencyHz(RigVfo vfo)
        {
            if (vfo is RigVfo.VfoA or RigVfo.Main)
                return ForcedMainReadHz ?? MainHz;

            return SubHz;
        }

        public bool SetFrequencyHz(long hz)
        {
            if (_currentVfo == RigVfo.Sub && FailSubWritesRemaining > 0)
            {
                FailSubWritesRemaining--;
                FailedSubWrites++;
                return false;
            }

            if (_currentVfo is RigVfo.VfoA or RigVfo.Main)
                MainHz = hz;
            else
                SubHz = hz;

            return true;
        }

        private RigVfo _currentVfo = RigVfo.Main;

        public void SelectVfo(RigVfo vfo, bool force = false) => _currentVfo = vfo;

        public void SetMode(string mode)
        {
        }

        public void SetSplitOn(bool on)
        {
        }

        public void SetSatelliteMode(bool on)
        {
        }

        public void ExchangeVfos()
        {
        }

        public void SetToneOn(bool on)
        {
        }

        public void SetToneSquelchOn(bool on)
        {
        }

        public void SetToneHz(double hz, bool squelchTone)
        {
        }

        public void Dispose()
        {
        }
    }
}
