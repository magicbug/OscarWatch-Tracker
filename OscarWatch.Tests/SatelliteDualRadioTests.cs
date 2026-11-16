using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class SatelliteDualRadioTests
{
    [Theory]
    [InlineData(RigType.IcomIc910)]
    [InlineData(RigType.IcomIc9100)]
    [InlineData(RigType.IcomIc9700)]
    [InlineData(RigType.IcomIc821h)]
    [InlineData(RigType.YaesuFt847)]
    [InlineData(RigType.KenwoodTs2000)]
    public void IsDualCapableSerialEndpoint_includes_satellite_radios(RigType type)
    {
        Assert.True(RigSettings.IsSatelliteDualEndpoint(type));
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(type));
    }

    [Fact]
    public void Flex_is_not_a_satellite_dual_endpoint()
    {
        Assert.False(RigSettings.IsSatelliteDualEndpoint(RigType.FlexSmartSdr));
        Assert.False(RigSettings.IsDualCapableSerialEndpoint(RigType.FlexSmartSdr));
    }

    [Theory]
    [InlineData(RigType.IcomIc910)]
    [InlineData(RigType.IcomIc9100)]
    [InlineData(RigType.IcomIc9700)]
    [InlineData(RigType.IcomIc821h)]
    [InlineData(RigType.YaesuFt847)]
    [InlineData(RigType.KenwoodTs2000)]
    public void Create_endpoint_builds_serial_satellite_driver(RigType type)
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = type,
            Port = "COM_SAT",
            BaudRate = 57600,
            CivAddress = RigSettings.DefaultCivAddressFor(type),
            KenwoodHardwareRtsEnabled = false
        });

        Assert.Equal(type, driver.RigType);
    }

    [Fact]
    public void Create_endpoint_ic9700_lan_builds_driver()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.IcomIc9700,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.168.1.60",
            NetworkPort = RigSettings.IcomLanDefaultPort,
            NetworkUser = "oscar",
            NetworkPassword = "secret",
            CivAddress = "A2"
        });

        Assert.Equal(RigType.IcomIc9700, driver.RigType);
    }

    [Fact]
    public void Ic9700_lan_endpoint_is_configured_without_com_port()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.IcomIc9700,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.168.1.60",
            NetworkPort = RigSettings.IcomLanDefaultPort,
            NetworkUser = "oscar",
            NetworkPassword = "secret"
        };

        Assert.True(RigSettings.IsIcomLanEndpoint(RigType.IcomIc9700));
        Assert.True(endpoint.UsesIcomLan);
        Assert.True(endpoint.IsConfigured);
        Assert.Equal("", endpoint.SerialPortInUse);
    }

    [Theory]
    [InlineData(RigType.IcomIc9700)]
    [InlineData(RigType.YaesuFt847)]
    [InlineData(RigType.KenwoodTs2000)]
    public void Dual_pass_init_turns_satellite_mode_off_then_tunes_main(RigType satelliteType)
    {
        var downRig = new RecordingRigDriver { RigType = satelliteType };
        var upRig = new RecordingRigDriver { RigType = RigType.YaesuFt817 };
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdFmHz = 200,
            Downlink = new RigEndpointSettings
            {
                Type = satelliteType,
                Port = "COM_DL",
                BaudRate = 57600,
                CatDelayMs = 0,
                CivAddress = RigSettings.DefaultCivAddressFor(satelliteType)
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt817,
                Port = "COM_UL",
                BaudRate = 4800,
                CatDelayMs = 0
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "27607",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.True(downRig.SetSatelliteModeCallCount >= 1);
        Assert.False(downRig.LastSatelliteModeOn);
        Assert.Equal(0, upRig.SetSatelliteModeCallCount);

        Assert.Equal(RigVfo.Main, downRig.CurrentVfo);
        Assert.True(downRig.SelectVfoCallCount >= 1);
        Assert.True(downRig.SetFrequencyCallCount >= 1);
        Assert.True(downRig.MainHz > 0);
        Assert.Equal(0, downRig.SubHz);
        Assert.True(controller.GetStatus().IsConnected);
        Assert.True(controller.GetStatus().IsTracking);
    }

    [Fact]
    public void Dual_pass_init_turns_satellite_mode_off_on_uplink_leg()
    {
        var downRig = new RecordingRigDriver { RigType = RigType.KenwoodTs990s };
        var upRig = new RecordingRigDriver { RigType = RigType.IcomIc9700 };
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdFmHz = 200,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.KenwoodTs990s,
                Port = "COM_DL",
                BaudRate = RigSettings.KenwoodHfDefaultBaudRate,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.IcomIc9700,
                Port = "COM_UL",
                BaudRate = RigSettings.Ic9700DefaultBaudRate,
                CatDelayMs = 0,
                CivAddress = "A2"
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "27607",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.Equal(0, downRig.SetSatelliteModeCallCount);
        Assert.True(upRig.SetSatelliteModeCallCount >= 1);
        Assert.False(upRig.LastSatelliteModeOn);
        Assert.Equal(RigVfo.Main, upRig.CurrentVfo);
        Assert.True(upRig.SetFrequencyCallCount >= 1);
        Assert.True(upRig.MainHz > 0);
    }
}
