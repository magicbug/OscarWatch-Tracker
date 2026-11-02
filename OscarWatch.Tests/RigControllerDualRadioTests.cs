using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class RigControllerDualRadioTests
{
    [Fact]
    public void Dual_pass_init_writes_rx_on_downlink_and_tx_on_uplink()
    {
        var downTransport = new RecordingYaesuCatTransport();
        var upTransport = new RecordingYaesuCatTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new YaesuFt817Driver(RigType.YaesuFt817, downTransport)
                : new YaesuFt818Driver(upTransport));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt817,
                Port = "COM_DL",
                BaudRate = 38400,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 38400,
                CatDelayMs = 0,
                Region = RigRegion.USA
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
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
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.DoesNotContain(downTransport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.SplitOn.ToArray()));
        Assert.Contains(downTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
        Assert.Contains(upTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
        Assert.Contains(upTransport.SentFrames, f => f.Length == 5 && f[4] == 0x0b);
    }

    [Fact]
    public void Dual_linear_full_doppler_updates_both_radios_when_range_rate_changes()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt817,
                Port = "COM_DL",
                BaudRate = 38400,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 38400,
                CatDelayMs = 0
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAtRest = downRig.MainHz;
        var txAtRest = upRig.MainHz;
        Assert.True(rxAtRest > 0);
        Assert.True(txAtRest > 0);

        controller.PublishContext(settings, Build(4.2));
        for (var i = 0; i < 8; i++)
            controller.RunTrackingLoopOnce();

        var expectedRx = (long)(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioReceiveKHz * 1000);
        var expectedTx = (long)(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioTransmitKHz * 1000);

        Assert.InRange(downRig.MainHz, expectedRx - 55, expectedRx + 55);
        Assert.InRange(upRig.MainHz, expectedTx - 55, expectedTx + 55);
        Assert.NotEqual(rxAtRest, downRig.MainHz);
        Assert.NotEqual(txAtRest, upRig.MainHz);
    }

    [Fact]
    public void Dual_linear_holds_downlink_cat_while_operator_spins_but_tracks_uplink()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "COM_DL", CatDelayMs = 0 },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "COM_UL", CatDelayMs = 0 }
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAfterInit = downRig.MainHz;
        var txAfterInit = upRig.MainHz;

        controller.PublishContext(settings, Build(4.2));
        for (var i = 0; i < 14; i++)
        {
            downRig.MainHz = rxAfterInit + 1_500 + i * 200;
            controller.RunTrackingLoopOnce();
        }

        Assert.Equal(rxAfterInit + 1_500 + 13 * 200, downRig.MainHz);
        Assert.NotEqual(txAfterInit, upRig.MainHz);
    }

    [Fact]
    public void Dual_linear_manual_downlink_offset_shifts_uplink_passband_rev()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "COM_DL", CatDelayMs = 0 },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "COM_UL", CatDelayMs = 0 }
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var txAfterInit = upRig.MainHz;
        // Capture stays held until a loop sees the dial still on the CAT frequency. The rig
        // thread is suspended for 500 ms of the wait above, so take that sample here.
        for (var i = 0; i < 3; i++)
            controller.RunTrackingLoopOnce();
        downRig.MainHz += 2_000;
        for (var i = 0; i < 10; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, 1.9, 2.1);
        Assert.InRange(status.ManualTransmitAdjustKHz, -2.1, -1.9);
        Assert.True(upRig.MainHz < txAfterInit, $"REV dual expects TX to drop when RX rises: tx={upRig.MainHz} was {txAfterInit}");
    }

    /// <summary>
    /// While the operator spins RX, dual mode still writes uplink Doppler. Those TX-only CAT writes must
    /// not refresh the RX dial settle window, or passband capture never runs and uplink stays untrimmed.
    /// </summary>
    [Fact]
    public void Dual_linear_passband_capture_survives_uplink_doppler_writes_during_knob_spin()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "COM_DL", CatDelayMs = 0 },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "COM_UL", CatDelayMs = 0 }
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAfterInit = downRig.MainHz;
        var txAfterInit = upRig.MainHz;

        // Spin RX while uplink Doppler keeps moving — each TX-only write used to refresh dial settle.
        for (var i = 0; i < 16; i++)
        {
            downRig.MainHz = rxAfterInit + 500 + i * 100;
            controller.PublishContext(settings, Build(0.4 + i * 0.35));
            controller.RunTrackingLoopOnce();
        }

        // Return to zero range-rate so dial offset maps cleanly to ~2 kHz passband trim.
        var spunRx = rxAfterInit + 2_000;
        downRig.MainHz = spunRx;
        controller.PublishContext(settings, Build(0));
        for (var i = 0; i < 12; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, 1.5, 2.5);
        Assert.InRange(status.ManualTransmitAdjustKHz, -2.5, -1.5);
        Assert.Equal(spunRx, downRig.MainHz);
        Assert.True(
            upRig.MainHz < txAfterInit - 1_000,
            $"Expected uplink passband trim after dial settle; tx={upRig.MainHz} init={txAfterInit} rxAdj={status.ManualReceiveAdjustKHz:F3}");
    }

    [Fact]
    public void Disconnect_clears_passband_trim()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL" ? downRig : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "COM_DL", CatDelayMs = 0 },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "COM_UL", CatDelayMs = 0 }
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            DopplerStrategy = DopplerStrategy.Full
        });
        Thread.Sleep(650);

        // Capture stays held until a loop sees the dial still on the CAT frequency. The rig
        // thread is suspended for 500 ms of the wait above, so take that sample here.
        for (var i = 0; i < 3; i++)
            controller.RunTrackingLoopOnce();

        downRig.MainHz += 2_000;
        for (var i = 0; i < 10; i++)
            controller.RunTrackingLoopOnce();

        Assert.InRange(controller.GetStatus().ManualReceiveAdjustKHz, 1.9, 2.1);

        controller.DisconnectAndWait();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);
    }

    [Fact]
    public void Mixed_ic705_downlink_and_ft818_uplink_pass_init_writes_both_legs()
    {
        var downTransport = new RecordingIcomCivTransport();
        var upTransport = new RecordingYaesuCatTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new IcomIc705Driver(downTransport)
                : new YaesuFt818Driver(upTransport));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc705,
                Port = "COM_DL",
                BaudRate = 115200,
                CivAddress = "A4",
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 4800,
                CatDelayMs = 0,
                Region = RigRegion.EU
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
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
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.Contains(downTransport.SentCommandBodies, b => b == "0700");
        Assert.Contains(upTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
    }

    [Fact]
    public void Mixed_ft818_downlink_and_ic706mkiig_uplink_pass_init_writes_both_legs()
    {
        var downTransport = new RecordingYaesuCatTransport();
        var upTransport = new RecordingIcomCivTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new YaesuFt818Driver(downTransport)
                : new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, upTransport));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_DL",
                BaudRate = 4800,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.IcomIc706MkiiG,
                Port = "COM_UL",
                BaudRate = 19200,
                CivAddress = "58",
                CatDelayMs = 0,
                Region = RigRegion.EU
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
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
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.Contains(downTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
        Assert.Contains(upTransport.SentCommandBodies, b => b == "0700");
        Assert.True(upTransport.SetFrequencyCommandCount > 0);
    }

    [Fact]
    public void Dual_ft818_downlink_ao07_mode_a_pass_init_writes_rx_and_tx()
    {
        var downTransport = new RecordingYaesuCatTransport();
        var upTransport = new RecordingYaesuCatTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new YaesuFt818Driver(downTransport)
                : new YaesuFt818Driver(upTransport));

        var mode = new SatelliteTransponderMode
        {
            Type = "Mode A",
            DownlinkKHz = 29_450,
            UplinkKHz = 145_900,
            DownlinkMode = "USB",
            UplinkMode = "USB",
            Doppler = "NOR"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_DL",
                BaudRate = 4800,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 4800,
                CatDelayMs = 0,
                Region = RigRegion.EU
            }
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "AO-07",
                NoradId = "07530",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            DopplerStrategy = DopplerStrategy.Full
        });

        var expectedRx = (long)(DopplerFrequencyCalculator.Compute(mode, 0, 0).RadioReceiveKHz * 1000);
        var expectedTx = (long)(DopplerFrequencyCalculator.Compute(mode, 0, 0).RadioTransmitKHz * 1000);

        Assert.Contains(downTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
        Assert.Contains(upTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);

        var downFreqCmd = downTransport.SentFrames.Last(f => f.Length == 5 && f[4] == 0x01);
        var upFreqCmd = upTransport.SentFrames.Last(f => f.Length == 5 && f[4] == 0x01);
        Assert.InRange(YaesuFt817CatCodec.DecodeFrequency10Hz(downFreqCmd), expectedRx - 10, expectedRx + 10);
        Assert.InRange(YaesuFt817CatCodec.DecodeFrequency10Hz(upFreqCmd), expectedTx - 10, expectedTx + 10);
    }

    [Fact]
    public void Dual_ic706mkiig_both_legs_linear_doppler_updates_both_radios()
    {
        var downTransport = new RecordingIcomCivTransport { MainHz = 145_960_000 };
        var upTransport = new RecordingIcomCivTransport { MainHz = 435_250_000 };
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, downTransport)
                : new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, upTransport));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc706MkiiG,
                Port = "COM_DL",
                BaudRate = 19200,
                CivAddress = "58",
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.IcomIc706MkiiG,
                Port = "COM_UL",
                BaudRate = 19200,
                CivAddress = "4C",
                CatDelayMs = 0
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAtRest = downTransport.MainHz;
        var txAtRest = upTransport.MainHz;
        Assert.True(rxAtRest > 0);
        Assert.True(txAtRest > 0);

        controller.PublishContext(settings, Build(4.2));
        for (var i = 0; i < 8; i++)
            controller.RunTrackingLoopOnce();

        var expectedRx = (long)(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioReceiveKHz * 1000);
        var expectedTx = (long)(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioTransmitKHz * 1000);

        Assert.InRange(downTransport.MainHz, expectedRx - 55, expectedRx + 55);
        Assert.InRange(upTransport.MainHz, expectedTx - 55, expectedTx + 55);
        Assert.NotEqual(rxAtRest, downTransport.MainHz);
        Assert.NotEqual(txAtRest, upTransport.MainHz);
    }

    [Fact]
    public void Mixed_ft991_downlink_and_ft818_uplink_pass_init_writes_both_legs()
    {
        var downTransport = new RecordingYaesuNewCatTransport();
        var upTransport = new RecordingYaesuCatTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Port == "COM_DL"
                ? new YaesuFt991Driver(RigType.YaesuFt991, downTransport)
                : new YaesuFt818Driver(upTransport));

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt991,
                Port = "COM_DL",
                BaudRate = 38400,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 4800,
                CatDelayMs = 0,
                Region = RigRegion.EU
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
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
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.Contains(downTransport.SentCommands, c => c.StartsWith("FA", StringComparison.Ordinal));
        Assert.Contains(upTransport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
    }

    [Fact]
    public void Dual_sdr_downlink_writes_rx_frequency_on_pass_init()
    {
        using var sdrServer = new RigCtlTcpStubServer();
        sdrServer.WaitUntilReady();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Type == RigType.SdrRigCtlTcp
                ? new RigCtlTcpDriver("127.0.0.1", sdrServer.Port)
                : upRig);

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.SdrRigCtlTcp,
                NetworkHost = "127.0.0.1",
                NetworkPort = sdrServer.Port,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt818,
                Port = "COM_UL",
                BaudRate = 38400,
                CatDelayMs = 0
            }
        };

        var context = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        var expectedRx = (long)(DopplerFrequencyCalculator.Compute(mode, 0, 0).RadioReceiveKHz * 1000);
        var initCompleted = false;
        // Allow time for connect backoff (3 s) plus rigctl round-trips on slower CI hosts.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            controller.Update(settings, context);
            if (sdrServer.FrequencyHz >= expectedRx - 5
                && sdrServer.FrequencyHz <= expectedRx + 5
                && upRig.MainHz > 0)
            {
                initCompleted = true;
                break;
            }

            Thread.Sleep(50);
        }

        Assert.True(initCompleted);
        Assert.InRange(sdrServer.FrequencyHz, expectedRx - 5, expectedRx + 5);
        Assert.True(upRig.MainHz > 0);
    }

    [Fact]
    public void Dual_sdr_downlink_ft991a_uplink_pass_init_writes_both_legs()
    {
        using var sdrServer = new RigCtlTcpStubServer();
        sdrServer.WaitUntilReady();
        var upTransport = new RecordingYaesuNewCatTransport();
        var controller = new RigController(
            endpointFactory: ep => ep.Type == RigType.SdrRigCtlTcp
                ? new RigCtlTcpDriver("127.0.0.1", sdrServer.Port)
                : new YaesuFt991aDriver(upTransport));

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.SdrRigCtlTcp,
                NetworkHost = "127.0.0.1",
                NetworkPort = sdrServer.Port,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt991a,
                Port = "COM_UL",
                BaudRate = 38400,
                CatDelayMs = 0
            }
        };

        var context = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        var expected = DopplerFrequencyCalculator.Compute(mode, 0, 0);
        var expectedRx = (long)(expected.RadioReceiveKHz * 1000);
        var expectedTx = (long)(expected.RadioTransmitKHz * 1000);
        var initCompleted = false;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            controller.Update(settings, context);
            var fbSet = upTransport.SentCommands.Find(c =>
                c.StartsWith("FB", StringComparison.Ordinal)
                && c.Length >= 11
                && long.TryParse(c.AsSpan(2, 9), out var hz)
                && Math.Abs(hz - expectedTx) <= 5);
            if (sdrServer.FrequencyHz >= expectedRx - 5
                && sdrServer.FrequencyHz <= expectedRx + 5
                && fbSet is not null
                && upTransport.SentCommands.Contains("FT3;"))
            {
                initCompleted = true;
                break;
            }

            Thread.Sleep(50);
        }

        Assert.True(initCompleted);
        Assert.InRange(sdrServer.FrequencyHz, expectedRx - 5, expectedRx + 5);
        Assert.Contains(upTransport.SentCommands, c => c == "FT3;");
        Assert.Contains(upTransport.SentCommands, c => c.StartsWith("FB", StringComparison.Ordinal));
    }

    [Fact]
    public void Dual_dummy_uplink_pass_init_writes_rx_only()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Type == RigType.SdrRigCtlTcp
                ? downRig
                : upRig);

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.SdrRigCtlTcp,
                NetworkHost = "127.0.0.1",
                NetworkPort = 4532,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.Dummy,
                CatDelayMs = 0,
                Region = RigRegion.USA
            }
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
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
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });

        Assert.True(downRig.MainHz > 0);
        Assert.Equal(0, upRig.MainHz);
        Assert.Equal(0, upRig.SetFrequencyCallCount);
        Assert.Null(upRig.LastToneHz);
    }

    [Fact]
    public void Dual_dummy_uplink_doppler_updates_rx_only()
    {
        var downRig = new RecordingRigDriver();
        var upRig = new RecordingRigDriver();
        var controller = new RigController(
            endpointFactory: ep => ep.Type == RigType.SdrRigCtlTcp
                ? downRig
                : upRig);

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice U/V",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_250,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            DopplerThresholdLinearHz = 50,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.SdrRigCtlTcp,
                NetworkHost = "127.0.0.1",
                NetworkPort = 4532,
                CatDelayMs = 0
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.Dummy,
                CatDelayMs = 0
            }
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 30, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            DopplerStrategy = DopplerStrategy.Full
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAtRest = downRig.MainHz;
        Assert.True(rxAtRest > 0);
        Assert.Equal(0, upRig.MainHz);
        Assert.Equal(0, upRig.SetFrequencyCallCount);

        controller.PublishContext(settings, Build(4.2));
        for (var i = 0; i < 8; i++)
            controller.RunTrackingLoopOnce();

        var expectedRx = (long)(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioReceiveKHz * 1000);

        Assert.InRange(downRig.MainHz, expectedRx - 55, expectedRx + 55);
        Assert.NotEqual(rxAtRest, downRig.MainHz);
        Assert.Equal(0, upRig.SetFrequencyCallCount);
    }
}
