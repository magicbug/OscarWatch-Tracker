using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodHfDualRadioTests
{
    [Fact]
    public void Transverter_legs_tune_their_if_from_sky_frequencies()
    {
        var downTransport = new RecordingKenwoodHfTransport("024")
        {
            TransverterStateReply = "XV1;",
            TransverterOffsetReply = "XO000116000000;"
        };
        var upTransport = new RecordingKenwoodHfTransport("023")
        {
            TransverterStateReply = "EX05600001;",
            TransverterOffsetReply = "XO000406000000;"
        };
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new KenwoodHfDriver(RigType.KenwoodTs890s, downTransport, catDelayMs: 0)
                : new KenwoodHfDriver(RigType.KenwoodTs590sg, upTransport, catDelayMs: 0));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings { Type = RigType.KenwoodTs890s, Port = "COM_DL", CatDelayMs = 0 },
            Uplink = new RigEndpointSettings { Type = RigType.KenwoodTs590sg, Port = "COM_UL", CatDelayMs = 0 }
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

        Assert.Contains("ID;", downTransport.SentCommands);
        Assert.Contains("OM04;", downTransport.SentCommands);
        Assert.Contains("FA00029960000;", downTransport.SentCommands);
        Assert.Contains("ID;", upTransport.SentCommands);
        Assert.Contains("MD4;", upTransport.SentCommands);
        Assert.Contains("FA00029250000;", upTransport.SentCommands);
    }
}
