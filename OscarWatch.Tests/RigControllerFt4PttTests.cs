using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public class RigControllerFt4PttTests
{
    [Fact]
    public void Cat_ptt_during_ft4_doppler_step_is_sent_before_the_next_frequency_write()
    {
        var rig = new BlockingFrequencyDriver();
        var controller = new RigController(_ => rig);
        try
        {
            controller.Update(FmSettings(), FmContext());
            controller.SetFt4SlotGatedDoppler(true);
            controller.DrainCommandQueueForTests();

            rig.ArmBlock = true;
            controller.ForceFt4DopplerStep();
            Assert.True(rig.Entered.Wait(TimeSpan.FromSeconds(3)), "Doppler step did not reach a frequency write");

            controller.SetPtt(true);
            rig.Release.Set();
            controller.DrainCommandQueueForTests();

            var events = rig.Snapshot();
            var ptt = events.IndexOf("ptt-on");
            Assert.True(ptt >= 0, "CAT PTT was not sent");
            Assert.Equal(1, events.Count(e => e == "ptt-on"));
            Assert.DoesNotContain(events.Skip(ptt + 1), e => e == "freq");
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
    public void Ft4_doppler_step_uses_range_rate_at_the_requested_instant()
    {
        var rig = new BlockingFrequencyDriver();
        var propagator = new InstantRecordingPropagator();
        var controller = new RigController(_ => rig, propagator: propagator);
        try
        {
            controller.Update(FmSettings(), FmContext());
            controller.SetFt4SlotGatedDoppler(true);
            controller.DrainCommandQueueForTests();
            propagator.Calls.Clear();

            var at = new DateTime(2026, 10, 9, 16, 50, 0, DateTimeKind.Utc);
            Assert.True(controller.TryForceFt4DopplerStep(at, TimeSpan.FromSeconds(3)));
            Assert.Contains(at, propagator.Calls);
        }
        finally
        {
            controller.Dispose();
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
        Corrected = new CorrectedFrequencies(145965, 435640, 145965, 435640, 0, false)
    };

    private sealed class BlockingFrequencyDriver : IRigDriver
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

    private sealed class InstantRecordingPropagator : IOrbitPropagator
    {
        public List<DateTime> Calls { get; } = [];

        public void Clear() { }
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 400);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 0);
        public bool HasSatellite(string noradId) => true;
        public IReadOnlyCollection<string> LoadedNoradIds => ["44909"];

        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc)
        {
            Calls.Add(utc);
            return new LookAngles(180, 20, 800, 1.5);
        }
    }
}
