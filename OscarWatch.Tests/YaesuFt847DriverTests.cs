using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class YaesuFt847DriverTests
{
    [Fact]
    public void Open_sends_cat_on()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt847Driver(transport);
        driver.Open();

        Assert.True(transport.SentFrames.Count >= 1);
        Assert.Equal(YaesuFt847CatCodec.CatOn.ToArray(), transport.SentFrames[0]);
    }

    [Fact]
    public void SetSatelliteMode_and_frequencies_use_sat_vfos()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt847Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(435_750_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(145_900_010);

        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt847CatCodec.SatelliteModeOn.ToArray()));
        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[4] == 0x11);
        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[4] == 0x21);
    }

    [Fact]
    public void SetMode_FM_sends_wide_fm_byte()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt847Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.SetMode("FM");

        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[0] == 0x08);
        Assert.DoesNotContain(transport.SentFrames, f => f.Length == 5 && f[0] == 0x88);
    }

    [Fact]
    public void SetMode_FMN_sends_narrow_fm_byte()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt847Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.SetMode("FMN");

        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[0] == 0x88);
    }

    [Fact]
    public void Pass_init_FM_sets_wide_fm_on_sat_rx_and_sat_tx()
    {
        var transport = new RecordingYaesuCatTransport();
        var controller = new RigController(_ => new YaesuFt847Driver(transport));
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt847,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "AO-91",
                NoradId = "43017",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });

        var modeFrames = transport.SentFrames
            .Where(f => f.Length == 5 && f[1] == 0 && f[2] == 0 && f[3] == 0 && f[4] is 0x17 or 0x27)
            .ToList();

        Assert.Equal(2, modeFrames.Count);
        Assert.All(modeFrames, f => Assert.Equal(0x08, f[0]));
        Assert.Contains(modeFrames, f => f[4] == 0x17);
        Assert.Contains(modeFrames, f => f[4] == 0x27);
    }

    [Fact]
    public void Pass_init_ISS_cross_band_repeater_uses_wide_fm_not_narrow()
    {
        var transport = new RecordingYaesuCatTransport();
        var controller = new RigController(_ => new YaesuFt847Driver(transport));
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt847,
            Port = "COM1",
            CatDelayMs = 0,
            Region = RigRegion.USA
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Cross band repeater",
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        var modeFrames = transport.SentFrames
            .Where(f => f.Length == 5 && f[1] == 0 && f[2] == 0 && f[3] == 0 && f[4] is 0x17 or 0x27)
            .ToList();

        Assert.Equal(2, modeFrames.Count);
        Assert.All(modeFrames, f => Assert.Equal(0x08, f[0]));
        Assert.DoesNotContain(transport.SentFrames, f => f.Length == 5 && f[0] == 0x88);
    }

    [Fact]
    public void Pass_init_FM_us_region_ctcss_uses_encode_only_on_sat_tx()
    {
        var transport = new RecordingYaesuCatTransport();
        var controller = new RigController(_ => new YaesuFt847Driver(transport));
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt847,
            Port = "COM1",
            CatDelayMs = 0,
            Region = RigRegion.USA
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        var encodeOn = YaesuFt847CatCodec.BuildCtcssOnCommand(
            encoderOnly: true,
            YaesuFt847VfoTarget.SatTx,
            satelliteMode: true);
        var decodeOn = YaesuFt847CatCodec.BuildCtcssOnCommand(
            encoderOnly: false,
            YaesuFt847VfoTarget.SatTx,
            satelliteMode: true);

        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(encodeOn));
        Assert.DoesNotContain(transport.SentFrames, f => f.SequenceEqual(decodeOn));
    }

    [Fact]
    public void SupportsVfoExchange_is_false()
    {
        var driver = new YaesuFt847Driver(new RecordingYaesuCatTransport());
        Assert.False(driver.SupportsVfoExchange);
    }

    [Fact]
    public void Dispose_sends_cat_off()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt847Driver(transport);
        driver.Open();
        driver.Dispose();
        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt847CatCodec.CatOff.ToArray()));
    }
}
