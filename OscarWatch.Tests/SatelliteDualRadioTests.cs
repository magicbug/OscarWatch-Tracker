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

    [Fact]
    public void Dual_ic821h_uplink_sends_07b0_before_mode_and_frequency_when_uplink_band_changes()
    {
        var downRig = new RecordingRigDriver { RigType = RigType.YaesuFt991a };
        var upTransport = new RecordingIcomCivTransport { MainHz = 145_900_000 };
        using var upRig = new IcomIc821hDriver(upTransport);
        using var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = CreateDualFt991aIc821hSettings();

        // 1. FO-29: VHF uplink (145.950 MHz LSB). Main is already on VHF, so no 07 B0 swap.
        var fo29 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_850,
            UplinkKHz = 145_950,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };
        controller.Update(settings, CreateContext("FO-29", "24278", fo29));
        controller.DrainCommandQueueForTests();

        Assert.DoesNotContain("1a0700", upTransport.SentCommandBodies);
        Assert.DoesNotContain("07b0", upTransport.SentCommandBodies);
        Assert.Contains("0600", upTransport.SentCommandBodies);
        Assert.Equal(145_950_000, upTransport.MainHz);

        // 2. AO-91: UHF uplink (435.250 MHz FMN). Uplink band changed VHF -> UHF:
        //    07 B0 (Main/Sub exchange) must go out before 06 05 (FM) and 05 ... (frequency).
        upTransport.SentCommandBodies.Clear();
        var ao91 = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR",
            CtcssHz = 67.0
        };
        controller.Update(settings, CreateContext("AO-91", "43017", ao91, selectedCtcssHz: 67.0));
        controller.DrainCommandQueueForTests();

        Assert.DoesNotContain("1a0700", upTransport.SentCommandBodies);
        var swapIdx = upTransport.SentCommandBodies.IndexOf("07b0");
        var modeIdx = upTransport.SentCommandBodies.IndexOf("0605");
        var freqIdx = upTransport.SentCommandBodies.FindIndex(cmd => cmd.StartsWith("05", StringComparison.Ordinal));
        Assert.True(swapIdx >= 0, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(modeIdx > swapIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(freqIdx > modeIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.Equal(435_250_000, upTransport.MainHz);

        // 3. AO-73: UHF uplink (435.140 MHz LSB). Same UHF band, so no 07 B0 swap, mode changes to LSB.
        upTransport.SentCommandBodies.Clear();
        var ao73 = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_140,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };
        controller.Update(settings, CreateContext("AO-73", "39444", ao73));
        controller.DrainCommandQueueForTests();

        Assert.DoesNotContain("07b0", upTransport.SentCommandBodies);
        Assert.Contains("0600", upTransport.SentCommandBodies);
        Assert.Equal(435_140_000, upTransport.MainHz);

        // 4. SO-50: VHF uplink (145.850 MHz FMN). Uplink band changed UHF -> VHF:
        //    07 B0 must go out before 06 05 (FM) and 05 ... (frequency).
        upTransport.SentCommandBodies.Clear();
        var so50 = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR",
            CtcssHz = 67.0
        };
        controller.Update(settings, CreateContext("SO-50", "27607", so50, selectedCtcssHz: 67.0));
        controller.DrainCommandQueueForTests();

        swapIdx = upTransport.SentCommandBodies.IndexOf("07b0");
        modeIdx = upTransport.SentCommandBodies.IndexOf("0605");
        freqIdx = upTransport.SentCommandBodies.FindIndex(cmd => cmd.StartsWith("05", StringComparison.Ordinal));
        Assert.True(swapIdx >= 0, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(modeIdx > swapIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(freqIdx > modeIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.Equal(145_850_000, upTransport.MainHz);
    }

    [Fact]
    public void Dual_ic821h_uplink_swaps_band_when_read_misses_after_beacon_pass()
    {
        var downRig = new RecordingRigDriver { RigType = RigType.YaesuFt991a };
        var upTransport = new RecordingIcomCivTransport { MainHz = 145_950_000 };
        using var upRig = new IcomIc821hDriver(upTransport);
        using var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = CreateDualFt991aIc821hSettings();

        var fo29 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_850,
            UplinkKHz = 145_950,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };
        controller.Update(settings, CreateContext("FO-29", "24278", fo29));
        controller.DrainCommandQueueForTests();

        var hadesBeacon = new SatelliteTransponderMode
        {
            Type = "Voice A/U downlink listen only",
            DownlinkKHz = 436_888,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };
        controller.Update(settings, CreateContext("HADES-L", "A0909", hadesBeacon));
        controller.DrainCommandQueueForTests();

        // Simulate a missed 0x03 read when switching from VHF (before the beacon) to UHF.
        upTransport.NextReadResponse = [];
        upTransport.SentCommandBodies.Clear();
        var ao73 = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_140,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };
        controller.Update(settings, CreateContext("AO-73", "39444", ao73));
        controller.DrainCommandQueueForTests();

        var swapIdx = upTransport.SentCommandBodies.IndexOf("07b0");
        var modeIdx = upTransport.SentCommandBodies.IndexOf("0600");
        Assert.True(swapIdx >= 0, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(modeIdx > swapIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.Equal(435_140_000, upTransport.MainHz);
    }

    [Fact]
    public void Dual_ic821h_uplink_recovers_with_07b0_when_initial_read_is_out_of_band_and_set_freq_naks()
    {
        var downRig = new RecordingRigDriver { RigType = RigType.YaesuFt991a };
        // Start with Main tuned to 162.550 MHz (NOAA weather), outside the amateur band check:
        // TryConfirmIdentity accepts it (>= 100 kHz), ReadFrequencyHz returns null, and the first
        // 435 MHz write NAKs until 07 B0 swaps 70 cm onto Main.
        var upTransport = new RecordingIcomCivTransport { MainHz = 162_550_000 };
        upTransport.SetFrequencyResponses.Enqueue([0xFE, 0xFE, 0x00, 0x4C, 0xFA, 0xFD]);
        upTransport.SetFrequencyResponses.Enqueue([0xFE, 0xFE, 0x00, 0x4C, 0xFA, 0xFD]);
        using var upRig = new IcomIc821hDriver(upTransport);
        using var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = CreateDualFt991aIc821hSettings();
        var ao91 = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR"
        };
        controller.Update(settings, CreateContext("AO-91", "43017", ao91));
        controller.DrainCommandQueueForTests();

        var swapIdx = upTransport.SentCommandBodies.IndexOf("07b0");
        var lastModeIdx = upTransport.SentCommandBodies.LastIndexOf("0605");
        var lastFreqIdx = upTransport.SentCommandBodies.FindLastIndex(cmd => cmd.StartsWith("05", StringComparison.Ordinal));
        Assert.True(swapIdx >= 0, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(lastModeIdx > swapIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.True(lastFreqIdx > lastModeIdx, string.Join(", ", upTransport.SentCommandBodies));
        Assert.Equal(435_250_000, upTransport.MainHz);
    }

    [Fact]
    public void Dual_icom_downlink_leg_swaps_main_band_before_mode_and_frequency()
    {
        var downRig = new RecordingRigDriver
        {
            RigType = RigType.IcomIc9700,
            MainHz = 145_900_000,
            SubHz = 435_800_000
        };
        var upRig = new RecordingRigDriver { RigType = RigType.YaesuFt817 };
        using var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdFmHz = 200,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc9700,
                Port = "COM_DL",
                BaudRate = RigSettings.Ic9700DefaultBaudRate,
                CatDelayMs = 0,
                CivAddress = "A2"
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt817,
                Port = "COM_UL",
                BaudRate = 4800,
                CatDelayMs = 0
            }
        };

        var so50 = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR"
        };
        controller.Update(settings, CreateContext("SO-50", "27607", so50));
        controller.DrainCommandQueueForTests();

        Assert.Equal(1, downRig.ExchangeVfoCallCount);
        Assert.Equal(436_795_000, downRig.MainHz);
        Assert.Equal(145_900_000, downRig.SubHz);
    }

    private static RigSettings CreateDualFt991aIc821hSettings() => new()
    {
        Enabled = true,
        DualRadioEnabled = true,
        DopplerThresholdFmHz = 100,
        DopplerThresholdLinearHz = 50,
        Downlink = new RigEndpointSettings
        {
            Type = RigType.YaesuFt991a,
            Port = "COM_DL",
            BaudRate = 38400,
            Region = RigRegion.USA,
            CatDelayMs = 0
        },
        Uplink = new RigEndpointSettings
        {
            Type = RigType.IcomIc821h,
            Port = "COM_UL",
            BaudRate = 19200,
            Region = RigRegion.USA,
            CatDelayMs = 0,
            CivAddress = "4C"
        }
    };

    private static RigTrackingContext CreateContext(
        string name,
        string noradId,
        SatelliteTransponderMode mode,
        double? selectedCtcssHz = null) => new()
    {
        TrackState = new SatelliteTrackState
        {
            Name = name,
            NoradId = noradId,
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 30, 800, 0)
        },
        Mode = mode,
        Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
        SelectedCtcssHz = selectedCtcssHz
    };
}
