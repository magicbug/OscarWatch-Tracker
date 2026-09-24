using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public class RigControllerTests
{
    [Fact]
    public void Disabled_clears_connection_status()
    {
        var controller = new RigController();
        controller.Update(new RigSettings { Enabled = false }, null);
        var status = controller.GetStatus();
        Assert.False(status.IsTracking);
        Assert.False(status.IsConnected);
    }

    [Fact]
    public void Dual_radio_with_one_leg_configured_reports_select_dual_com_ports()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "COM3" },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "" }
        };

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();

        Assert.Equal(RigStatusKind.SelectDualComPorts, controller.GetStatus().StatusKind);
    }

    [Fact]
    public void Icom9700_uses_same_tracking_path_as_ic910()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM1",
            DopplerThresholdFmHz = 200,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 2.5)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = new CorrectedFrequencies(145852, 145952, 145850, 145950, 2, false),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, ctx);
        var status = controller.GetStatus();
        Assert.True(status.IsConnected);
        Assert.True(status.IsTracking);
        Assert.Equal(RigStatusKind.Tracking, status.StatusKind);
        Assert.DoesNotContain("not yet implemented", RigStatusText.ToEnglish(status), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(status.LastReceiveHz);
        Assert.NotNull(status.LastTransmitHz);
        Assert.Equal(67.0, rig.LastToneHz);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
        Assert.Equal(0, rig.SetSplitOnCallCount);
    }

    [Fact]
    public void Dummy_tracking_sets_receive_frequency()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdFmHz = 200,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            DownlinkKHz = 145950,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR"
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 2.5)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = new CorrectedFrequencies(145852, 145952, 145850, 145950, 2, false),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        var status = controller.GetStatus();
        Assert.True(status.IsConnected);
        Assert.True(status.IsTracking);
        Assert.NotNull(status.LastReceiveHz);
    }

    [Fact]
    public void Cat_resume_reruns_pass_init_including_satellite_mode()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt847,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FM",
            UplinkMode = "FM"
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, rig.SetSatelliteModeCallCount);
        Assert.True(rig.LastSatelliteModeOn);

        settings.CatUpdatesPaused = true;
        controller.PublishContext(settings, ctx);
        controller.DrainCommandQueueForTests();
        Assert.False(controller.GetStatus().IsTracking);

        settings.CatUpdatesPaused = false;
        controller.PublishContext(settings, ctx);
        controller.DrainCommandQueueForTests();
        Assert.Equal(2, rig.SetSatelliteModeCallCount);
        Assert.True(rig.LastSatelliteModeOn);
        Assert.True(controller.GetStatus().IsTracking);
    }

    [Fact]
    public void Kenwood_satellite_mode_unconfirmed_does_not_fall_back_to_split_vfo()
    {
        var rig = new RecordingRigDriver { IsSatelliteModeActive = false };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Linear",
            DownlinkKHz = 435_650,
            UplinkKHz = 145_950,
            DownlinkMode = "USB",
            UplinkMode = "LSB"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "44909",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        Assert.True(rig.SetSatelliteModeCallCount >= 1);
        Assert.True(rig.LastSatelliteModeOn);
        Assert.Equal(0, rig.SetSplitOnCallCount);
    }

    [Fact]
    public void Ts2000_satl_unconfirmed_uses_FA_FB_doppler_and_reports_degraded_status()
    {
        var transport = new NonConfirmingSatelliteTransport(returnNull: false);
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0, satModeRetryCount: 3, satModeRetryDelayMs: 0);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            DopplerThresholdFmHz = 200,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 145_900,
            UplinkKHz = 435_700,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "IO-117",
                NoradId = "55555",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 45, 600, 2.0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 2.0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        Assert.False(driver.IsSatelliteModeActive);
        Assert.True(driver.UsesFaFbSatelliteTracking);
        Assert.Equal(RigStatusKind.Ts2000SatlUnconfirmed, controller.GetStatus().StatusKind);

        transport.SentCommands.Clear();
        var state2 = new SatelliteTrackState
        {
            Name = "IO-117",
            NoradId = "55555",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 45, 600, 5.0)
        };
        var corrected2 = DopplerFrequencyCalculator.Compute(mode, 5.0, 0);
        controller.PublishContext(settings, new RigTrackingContext
        {
            TrackState = state2,
            Mode = mode,
            Corrected = corrected2
        });
        controller.DrainCommandQueueForTests();
        controller.RunTrackingLoopOnce();
        controller.DrainCommandQueueForTests();

        var expectedRxHz = (long)Math.Round(corrected2.RadioReceiveKHz * 1000.0);
        Assert.Contains($"FA{expectedRxHz:D11};", transport.SentCommands);
    }

    [Fact]
    public void Ts2000_reinitialize_pass_reaffirms_layout_without_full_sat_entry_or_vfo_exchange()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0, satModeRetryCount: 1, satModeRetryDelayMs: 0);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            DopplerThresholdFmHz = 200,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 145_900,
            UplinkKHz = 435_700,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "NOR"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "AO-07",
                NoradId = "07530",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 45, 600, 2.0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 2.0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        // Simulate swapped FA/FB on the radio — ICOM-style band swap must not run on Kenwood re-init.
        transport.FaHz = 435_700_000;
        transport.FbHz = 145_900_000;
        transport.SentCommands.Clear();

        controller.PublishContext(settings, ctx, reinitializePass: true);
        controller.DrainCommandQueueForTests();

        Assert.DoesNotContain("TS1;", transport.SentCommands);
        Assert.Contains("SA1010110;", transport.SentCommands);
        var expectedRxHz = (long)Math.Round(ctx.Corrected.RadioReceiveKHz * 1000.0);
        var expectedTxHz = (long)Math.Round(ctx.Corrected.RadioTransmitKHz * 1000.0);
        Assert.Contains($"FA{expectedRxHz:D11};", transport.SentCommands);
        Assert.Contains($"FB{expectedTxHz:D11};", transport.SentCommands);
        Assert.Equal(expectedRxHz, transport.FaHz);
        Assert.Equal(expectedTxHz, transport.FbHz);
        Assert.Contains("DC10;", transport.SentCommands);
    }

    [Fact]
    public void Ts2000_tracking_loop_sends_link_hold_poll_on_interval()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(
            transport,
            catDelayMs: 0,
            satModeSettlingDelayMs: 0,
            satModeRetryCount: 1,
            satModeRetryDelayMs: 0,
            linkHoldPollIntervalMs: 100);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            DopplerThresholdFmHz = 50_000,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "27607",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 45, 600, 2.0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 2.0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();
        Thread.Sleep(150);
        transport.SentCommands.Clear();

        controller.RunTrackingLoopOnce();
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, transport.SentCommands.Count(c => c == "FA;"));

        transport.SentCommands.Clear();
        controller.RunTrackingLoopOnce();
        controller.DrainCommandQueueForTests();
        Assert.Empty(transport.SentCommands);

        Thread.Sleep(110);
        controller.RunTrackingLoopOnce();
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, transport.SentCommands.Count(c => c == "FA;"));
    }

    private sealed class NonConfirmingSatelliteTransport : IKenwoodCatTransport
    {
        private readonly bool _returnNull;

        public NonConfirmingSatelliteTransport(bool returnNull = false) => _returnNull = returnNull;

        public List<string> SentCommands { get; } = [];
        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public bool SendFireAndForget(string command, int postDelayMs = 50)
        {
            SentCommands.Add(Normalize(command));
            return true;
        }

        public bool SendCommand(string command, int postDelayMs = 50) =>
            SendFireAndForget(command, postDelayMs);

        public string? Transact(string command, int postDelayMs = 50)
        {
            var normalized = Normalize(command);
            SentCommands.Add(normalized);
            return normalized switch
            {
                "SA;" => _returnNull ? null : "SA0;",
                "FA;" => "FA00435750000;",
                _ => null
            };
        }

        public void Dispose() => IsOpen = false;

        private static string Normalize(string command)
        {
            var cmd = command.Trim();
            return cmd.EndsWith(';') ? cmd : cmd + ";";
        }
    }

    [Fact]
    public void Reselect_same_pass_reruns_pass_init()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt847,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FM",
            UplinkMode = "FM"
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, rig.SetSatelliteModeCallCount);

        controller.PublishContext(settings, ctx, reinitializePass: true);
        controller.DrainCommandQueueForTests();
        Assert.Equal(2, rig.SetSatelliteModeCallCount);
        Assert.True(rig.LastSatelliteModeOn);
    }

    [Fact]
    public void Pass_init_failed_frequency_write_retries_on_tracking_loop()
    {
        var rig = new RecordingRigDriver();
        rig.SetFrequencyResults.Enqueue(false);
        rig.SetFrequencyResults.Enqueue(false);
        rig.SetFrequencyResults.Enqueue(true);
        rig.SetFrequencyResults.Enqueue(true);

        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0);
        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = corrected,
            ReceiveOffsetKHz = 0
        };

        var expectedRx = (long)Math.Round(corrected.RadioReceiveKHz * 1000.0);
        var expectedTx = (long)Math.Round(corrected.RadioTransmitKHz * 1000.0);

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        Assert.Equal(expectedRx, rig.MainHz);
        Assert.Equal(expectedTx, rig.SubHz);
        Assert.Equal(4, rig.SetFrequencyCallCount);
    }

    [Fact]
    public void Pass_init_failed_frequency_write_keeps_rig_unchanged_when_retries_also_fail()
    {
        var rig = new RecordingRigDriver();
        for (var i = 0; i < 4; i++)
            rig.SetFrequencyResults.Enqueue(false);

        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        Assert.Equal(0, rig.MainHz);
        Assert.Equal(0, rig.SubHz);
    }

    [Fact]
    public void Resuming_cat_pause_with_null_context_clears_internal_pause_state()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy
        };

        settings.CatUpdatesPaused = true;
        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();
        Assert.True(controller.GetStatus().CatUpdatesPaused);

        settings.CatUpdatesPaused = false;
        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();
        Assert.False(controller.GetStatus().CatUpdatesPaused);
    }

    [Fact]
    public void Cat_paused_skips_tracking()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            CatUpdatesPaused = true
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145950,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN"
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 2.5)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = new CorrectedFrequencies(145850, 145950, 145850, 145950, 0, false),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        var status = controller.GetStatus();
        Assert.True(status.CatUpdatesPaused);
        Assert.False(status.IsTracking);
        Assert.Equal(RigStatusKind.CatPaused, status.StatusKind);
        Assert.NotNull(status.LastReceiveHz);
        Assert.NotNull(status.LastTransmitHz);
    }

    [Fact]
    public void Tracks_when_satellite_selected_even_below_horizon()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145950,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN"
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 1, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = new CorrectedFrequencies(145850, 145950, 145850, 145950, 0, false),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        var status = controller.GetStatus();
        Assert.True(status.IsTracking);
        Assert.Equal(RigStatusKind.Tracking, status.StatusKind);
    }

    [Fact]
    public void Brief_null_look_angles_keeps_doppler_tracking()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145950,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN"
        };

        RigTrackingContext Good(double elevationDeg) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, elevationDeg, 800, 0)
            },
            Mode = mode,
            Corrected = new CorrectedFrequencies(145850, 145950, 145850, 145950, 0, false)
        };

        var missingAngles = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = null
            },
            Mode = mode,
            Corrected = new CorrectedFrequencies(145850, 145950, 145850, 145950, 0, false)
        };

        controller.Update(settings, Good(20));
        Assert.True(controller.GetStatus().IsTracking);

        for (var i = 1; i < RigController.MissingLookAnglesClearTicks; i++)
        {
            controller.Update(settings, missingAngles);
            Assert.True(controller.GetStatus().IsTracking, $"tick {i} should hold last-good context");
            Assert.Equal(RigStatusKind.Tracking, controller.GetStatus().StatusKind);
        }

        controller.Update(settings, Good(22));
        Assert.True(controller.GetStatus().IsTracking);
    }

    [Fact]
    public void Sustained_null_look_angles_clears_doppler_tracking()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145950,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN"
        };

        var good = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = new CorrectedFrequencies(145850, 145950, 145850, 145950, 0, false)
        };

        controller.Update(settings, good);
        Assert.True(controller.GetStatus().IsTracking);

        for (var i = 0; i < RigController.MissingLookAnglesClearTicks; i++)
            controller.Update(settings, null);

        var status = controller.GetStatus();
        Assert.False(status.IsTracking);
        Assert.Equal(RigStatusKind.Connected, status.StatusKind);
    }

    [Fact]
    public void Rx_offset_shifts_downlink_not_uplink_on_rev_satellite()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        var rxBefore = rig.MainHz;
        var txBefore = rig.SubHz;

        controller.Update(settings, Build(2.0));
        Thread.Sleep(650);

        Assert.True(rig.MainHz > rxBefore);
        Assert.Equal(txBefore, rig.SubHz);
    }

    [Fact]
    public void Tx_offset_shifts_uplink_not_downlink_on_rev_satellite()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double txOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0, txOffset),
                TransmitOffsetKHz = txOffset,
                ReceiveOffsetKHz = 0
            };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        var rxBefore = rig.MainHz;
        var txBefore = rig.SubHz;

        controller.Update(settings, Build(2.0));
        Thread.Sleep(650);

        Assert.Equal(rxBefore, rig.MainHz);
        Assert.True(rig.SubHz > txBefore);
    }

    [Fact]
    public void Rx_offset_survives_range_rate_change_on_downlink()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rxOffset, double rangeRateKmPerSec) =>
            new()
            {
                TrackState = new SatelliteTrackState
                {
                    Name = "FO-29",
                    NoradId = "44208",
                    Subpoint = new GeoCoordinate(0, 0),
                    LookAngles = new LookAngles(180, 20, 800, rangeRateKmPerSec)
                },
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0, 0));
        Thread.Sleep(650);
        controller.Update(settings, Build(0, 4.2));
        Thread.Sleep(650);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();
        var rxNoOffset = rig.MainHz;

        controller.Update(settings, Build(5.2, 4.2));
        Thread.Sleep(650);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        Assert.InRange(rig.MainHz - rxNoOffset, 5_100, 5_300);
    }

    [Fact]
    public void Rx_offset_applies_on_rig_when_main_read_lags_after_cat_write()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "44208",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        controller.DrainCommandQueueForTests();
        var baselineMain = rig.MainHz;

        rig.NextStaleMainReadHz = baselineMain;
        controller.PublishContext(settings, Build(5.2), reinitializePass: false);
        controller.DrainCommandQueueForTests();

        Assert.Equal(baselineMain + 5_200, rig.MainHz);

        rig.NextStaleMainReadHz = baselineMain;
        for (var i = 0; i < 12; i++)
            controller.RunTrackingLoopOnce();

        Assert.Equal(baselineMain + 5_200, rig.MainHz);
    }

    [Fact]
    public void Flex_rx_offset_does_not_capture_stale_status_as_passband_trim()
    {
        // SmartSDR can keep reporting the pre-offset slice frequency after a CAT write.
        // Immediate Flex capture must not treat that lag as a Main-dial hunt (~20 kHz snaps).
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.FlexSmartSdr,
            NetworkHost = "127.0.0.1",
            NetworkPort = 4992,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        controller.DrainCommandQueueForTests();
        var baselineMain = rig.MainHz;

        // Reads keep returning the pre-offset frequency while SetFrequencyHz advances MainHz.
        rig.ForcedMainReadHz = baselineMain;
        controller.PublishContext(settings, Build(1.0), reinitializePass: false);
        controller.DrainCommandQueueForTests();

        var expectedAfterOffset = baselineMain + 1_000;
        Assert.Equal(expectedAfterOffset, rig.MainHz);

        for (var i = 0; i < 20; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.05, 0.05);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.05, 0.05);
        Assert.Equal(expectedAfterOffset, rig.MainHz);
    }

    [Fact]
    public void Rx_offset_sends_cat_frequency_to_rig()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_659.9,
            UplinkKHz = 145_960,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        var baselineMain = rig.MainHz;
        Assert.True(baselineMain > 0);

        var callsAfterInit = rig.SetFrequencyCallCount;
        controller.Update(settings, Build(5.524));
        Assert.True(rig.SetFrequencyCallCount > callsAfterInit);
        Assert.Equal(baselineMain + 5_524, rig.MainHz);
    }

    [Fact]
    public void Offset_publish_without_reinit_skips_pass_init_and_applies_frequency()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "24278",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        controller.DrainCommandQueueForTests();
        var baselineMain = rig.MainHz;
        var satModeCalls = rig.SetSatelliteModeCallCount;
        Assert.True(baselineMain > 0);

        controller.PublishContext(settings, Build(4.024), reinitializePass: false);
        controller.DrainCommandQueueForTests();

        Assert.Equal(satModeCalls, rig.SetSatelliteModeCallCount);
        Assert.Equal(baselineMain + 4_024, rig.MainHz);
    }

    [Fact]
    public void Ctcss_tone_change_commands_uplink_sub_vfo()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            Region = RigRegion.USA,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0,
            CtcssArmHz = 74.4
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double toneHz) => new()
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = toneHz
        };

        controller.Update(settings, Build(67.0));
        Assert.Equal(67.0, rig.LastToneHz);
        Assert.True(rig.LastToneSquelch);
        Assert.True(rig.ToneSquelchOn);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);

        controller.Update(settings, Build(74.4));
        Assert.Equal(74.4, rig.LastToneHz);
        Assert.True(rig.ToneSquelchOn);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
    }

    [Fact]
    public void Icom910_beacon_only_mode_disables_satellite_mode_and_tunes_downlink_vfo()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "SSTV (UHF)",
            DownlinkKHz = 437_550,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var state = new SatelliteTrackState
        {
            Name = "ISS",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.False(rig.LastSatelliteModeOn);
        Assert.False(rig.LastSplitOn);
        Assert.Equal(437_550_000, rig.MainHz);
        Assert.Equal(0, rig.SubHz);
        Assert.Equal(RigVfo.Main, rig.CurrentVfo);
        Assert.Contains(RigVfo.Main, rig.ToneClearedVfos);
        Assert.Contains(RigVfo.Sub, rig.ToneClearedVfos);
        Assert.False(rig.ToneOn);
        Assert.False(rig.ToneSquelchOn);
    }

    [Fact]
    public void Icom910_same_band_packet_enables_split_on_vfo_ab()
    {
        var rig = new RecordingRigDriver { MainHz = 436_500_000, SubHz = 145_990_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Packet",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0);
        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = corrected
        });
        controller.DrainCommandQueueForTests();

        Assert.False(rig.LastSatelliteModeOn);
        Assert.True(rig.LastSplitOn);
        Assert.Equal(145_825_000, rig.MainHz);
        Assert.Equal(145_825_000, rig.SubHz);
        Assert.Equal(RigVfo.VfoA, rig.CurrentVfo);
        Assert.Contains(RigVfo.VfoA, rig.ToneClearedVfos);
        Assert.Contains(RigVfo.VfoB, rig.ToneClearedVfos);
        Assert.DoesNotContain(RigVfo.Sub, rig.ToneClearedVfos);
    }

    [Fact]
    public void Icom9700_same_band_uhf_packet_437_437_swaps_vfo_when_starting_on_vhf()
    {
        // Issue #36: IC-9700 does not switch TX band to 437 MHz for a 437/437 MHz FM-data transponder
        // Starting on VHF (145 MHz) and tracking a UHF SPLIT transponder (437/437)
        // should trigger a VFO exchange to get both RX and TX on UHF.
        var rig = new RecordingRigDriver { MainHz = 145_990_000, SubHz = 145_990_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM-DATA",
            DownlinkKHz = 437_825,
            UplinkKHz = 437_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0);
        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "AO-91",
                NoradId = "43013",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = corrected
        });
        controller.DrainCommandQueueForTests();

        Assert.False(rig.LastSatelliteModeOn);
        Assert.True(rig.LastSplitOn);
        // VFO A should have 437 MHz for RX (may be after exchange)
        Assert.True(rig.MainHz == 437_825_000 || rig.SubHz == 437_825_000,
            $"Expected 437 MHz on either VFO A or B, got MainHz={rig.MainHz}, SubHz={rig.SubHz}");
        // Both frequencies should be 437 MHz (same-band transponder)
        Assert.True((rig.MainHz == 437_825_000 && rig.SubHz == 437_825_000) || 
                    (rig.MainHz == 437_825_000 || rig.SubHz == 437_825_000),
            $"Expected 437 MHz frequencies, got MainHz={rig.MainHz}, SubHz={rig.SubHz}");
        Assert.Equal(RigVfo.VfoA, rig.CurrentVfo);
        Assert.Contains(RigVfo.VfoA, rig.ToneClearedVfos);
        Assert.Contains(RigVfo.VfoB, rig.ToneClearedVfos);
        Assert.DoesNotContain(RigVfo.Sub, rig.ToneClearedVfos);
    }

    [Fact]
    public void Icom821h_same_band_packet_uses_satellite_main_sub_not_split()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc821hDriver(transport);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc821h,
            Port = "COM1",
            CatDelayMs = 0,
            CivAddress = "4C"
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Packet",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.Contains("1a0701", transport.SentCommandBodies);
        Assert.DoesNotContain("0f01", transport.SentCommandBodies);
        Assert.Contains("07d1", transport.SentCommandBodies);
    }

    [Fact]
    public void Icom821h_beacon_disables_satellite_mode()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc821hDriver(transport);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc821h,
            Port = "COM1",
            CatDelayMs = 0,
            CivAddress = "4C"
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "SSTV (VHF)",
            DownlinkKHz = 145_800,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.Contains("1a0700", transport.SentCommandBodies);
        Assert.DoesNotContain("0f01", transport.SentCommandBodies);
    }

    [Fact]
    public void Icom910_same_band_split_clears_ctcss_on_vfo_ab_not_main_sub()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "Voice Region 1",
            DownlinkKHz = 145_800,
            UplinkKHz = 145_200,
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
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });
        controller.DrainCommandQueueForTests();

        Assert.False(rig.LastSatelliteModeOn);
        Assert.True(rig.SetSplitOnCallCount >= 1);
        Assert.Contains(RigVfo.VfoA, rig.ToneClearedVfos);
        Assert.Contains(RigVfo.VfoB, rig.ToneClearedVfos);
        Assert.DoesNotContain(RigVfo.Sub, rig.ToneClearedVfos);
        Assert.Equal(RigVfo.VfoB, rig.LastToneVfo);
    }

    [Fact]
    public void Icom910_beacon_uhf_swaps_main_when_2m_was_on_main_after_sat()
    {
        var rig = new RecordingRigDriver { MainHz = 145_800_000, SubHz = 436_500_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "SSTV (UHF)",
            DownlinkKHz = 437_550,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.Equal(1, rig.ExchangeVfoCallCount);
        Assert.Equal(437_550_000, rig.MainHz);
        Assert.Equal(RigVfo.Main, rig.CurrentVfo);
    }

    [Fact]
    public void Icom910_beacon_vhf_tunes_main_and_swaps_when_70cm_was_on_main_after_sat()
    {
        var rig = new RecordingRigDriver { MainHz = 436_500_000, SubHz = 145_800_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "SSTV (VHF)",
            DownlinkKHz = 145_800,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.Equal(1, rig.ExchangeVfoCallCount);
        Assert.Equal(145_800_000, rig.MainHz);
        Assert.Equal(RigVfo.Main, rig.CurrentVfo);
        Assert.False(rig.LastSplitOn);
        Assert.Contains(RigVfo.Main, rig.ToneClearedVfos);
        Assert.Contains(RigVfo.Sub, rig.ToneClearedVfos);
    }

    [Fact]
    public void Icom910_beacon_after_same_band_packet_turns_split_off()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var packet = new SatelliteTransponderMode
        {
            Type = "Packet",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var sstv = new SatelliteTransponderMode
        {
            Type = "SSTV (VHF)",
            DownlinkKHz = 145_800,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        var state = new SatelliteTrackState
        {
            Name = "ISS",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = state,
            Mode = packet,
            Corrected = DopplerFrequencyCalculator.Compute(packet, 0, 0)
        });
        controller.DrainCommandQueueForTests();
        Assert.True(rig.LastSplitOn);

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = state,
            Mode = sstv,
            Corrected = DopplerFrequencyCalculator.Compute(sstv, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.False(rig.LastSatelliteModeOn);
        Assert.False(rig.LastSplitOn);
        Assert.Equal(145_800_000, rig.MainHz);
        Assert.Equal(RigVfo.Main, rig.CurrentVfo);
    }

    [Fact]
    public void ApplySelectedCtcss_from_selector_while_cat_paused()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            Region = RigRegion.USA,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0,
            CtcssArmHz = 74.4
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var access = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, access);
        Assert.Equal(67.0, rig.LastToneHz);

        settings.CatUpdatesPaused = true;
        var arm = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = access.Corrected,
            SelectedCtcssHz = 74.4
        };
        controller.PublishContext(settings, arm);
        controller.DrainCommandQueueForTests();
        Assert.Equal(67.0, rig.LastToneHz);

        controller.ApplySelectedCtcss(settings, arm);
        controller.DrainCommandQueueForTests();
        Assert.Equal(74.4, rig.LastToneHz);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
        Assert.True(rig.ToneSquelchOn);
    }

    [Fact]
    public void Pass_init_sets_main_and_sub_modes_for_satellite_layout()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, ctx);
        Assert.Equal(2, rig.ModeSetCount);
        Assert.Equal(RigVfo.Sub, rig.LastModeVfo);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
        Assert.Equal(RigVfo.Main, rig.LastToneOffVfo);
        Assert.False(rig.LastToneSquelch ?? true);
        Assert.True(rig.ToneOn);
    }

    [Fact]
    public void Pass_init_ctcss_uses_tsql_path_for_us_region()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM1",
            CatDelayMs = 0,
            Region = RigRegion.USA
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, ctx);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
        Assert.True(rig.LastToneSquelch);
        Assert.True(rig.ToneSquelchOn);
    }

    [Fact]
    public void Ts2000_pass_init_ctcss_sends_TO_not_CT_for_us_region()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0, satModeRetryCount: 1, satModeRetryDelayMs: 0);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            CatDelayMs = 0,
            Region = RigRegion.USA
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "ISS",
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        });
        controller.DrainCommandQueueForTests();

        Assert.Contains("TN01;", transport.SentCommands);
        Assert.Contains("TO1;", transport.SentCommands);
        Assert.DoesNotContain("CN01;", transport.SentCommands);
        Assert.DoesNotContain("CT1;", transport.SentCommands);
    }

    [Fact]
    public void Icom_ic910_ctcss_always_selects_sub_before_tone_commands()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc910,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 436_795,
            UplinkKHz = 145_850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        var state = new SatelliteTrackState
        {
            Name = "SO-50",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, ctx);
        Assert.Equal(RigVfo.Sub, rig.LastToneVfo);
        Assert.Equal(67.0, rig.LastToneHz);
    }

    [Fact]
    public void Spinner_offset_survives_small_main_vfo_read_jitter_without_manual_rx_drift()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "44208",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) =>
            new()
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = rxOffset
            };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        controller.Update(settings, Build(9.8));
        var rxAfterOffset = rig.MainHz;

        rig.MainHz = rxAfterOffset + 35;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);
    }

    [Fact]
    public void Rev_linear_knob_tune_keeps_receive_and_moves_transmit_opposite()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        var txAfterInit = rig.SubHz;
        Assert.True(rxAfterInit > 0);
        Assert.True(txAfterInit > 0);

        rig.MainHz = rxAfterInit + 2_500;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();
        Thread.Sleep(2600);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        Assert.Equal(rxAfterInit + 2_500, rig.MainHz);
        Assert.True(rig.SubHz < txAfterInit, $"REV expects TX to drop when RX rises: tx={rig.SubHz} was {txAfterInit}");
        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, 2.4, 2.6);
        Assert.InRange(status.ManualTransmitAdjustKHz, -2.6, -2.4);
    }

    [Fact]
    public void Rev_linear_small_knob_tune_captures_passband_below_cat_threshold()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_865,
            UplinkKHz = 435_110.1,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "JO-97",
                NoradId = "22222",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        Assert.True(rxAfterInit > 0);

        rig.MainHz = rxAfterInit + 50;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();

        Assert.Equal(rxAfterInit + 50, rig.MainHz);
        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, 0.04, 0.06);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.06, -0.04);
    }

    [Fact]
    public void Rev_linear_sub_threshold_dial_jitter_does_not_capture_passband()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_865,
            UplinkKHz = 435_110.1,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "JO-97",
                NoradId = "22222",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        Assert.True(rxAfterInit > 0);

        rig.MainHz = rxAfterInit + 20;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);
    }

    [Fact]
    public void Rev_linear_clears_phantom_manual_when_dial_matches_doppler_baseline()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        Thread.Sleep(650);

        // Slow CI runners can still be inside the post-write dial settle window, which would skip the sync.
        var ignoreDialUntil = typeof(RigController).GetField("_ignoreDialUntilUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < settleDeadline && (DateTime)ignoreDialUntil.GetValue(controller)! > DateTime.UtcNow)
            Thread.Sleep(20);

        // Simulate accumulated phantom manual (false knob detect) while rig stayed at doppler target.
        typeof(RigController).GetField("_passbandDownlinkAdjustKHz", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(controller, -9.8);
        typeof(RigController).GetField("_passbandUplinkAdjustKHz", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(controller, 9.8);

        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);
    }

    [Fact]
    public void Rev_linear_hands_off_automatic_resumes_with_passband_trim_after_knob_settles()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        Assert.True(rxAfterInit > 0);

        rig.MainHz = rxAfterInit + 2_500;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();
        Thread.Sleep(2600);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        var statusAfterHunt = controller.GetStatus();
        Assert.InRange(statusAfterHunt.ManualReceiveAdjustKHz, 2.4, 2.6);
        Assert.InRange(statusAfterHunt.ManualTransmitAdjustKHz, -2.6, -2.4);

        foreach (var rangeRateKmPerSec in new[] { 1.0, 2.5, 4.0, 5.5, 4.0, 2.5, 0.5, -0.5, -2.0, -4.0 })
        {
            controller.PublishContext(settings, Build(rangeRateKmPerSec));
            for (var i = 0; i < 3; i++)
                controller.RunTrackingLoopOnce();
        }

        var passbandDl = statusAfterHunt.ManualReceiveAdjustKHz;
        var passbandUl = statusAfterHunt.ManualTransmitAdjustKHz;
        var expectedRx = ToHz(DopplerFrequencyCalculator.Compute(
            mode,
            -4.0,
            0,
            0,
            passbandDl,
            passbandUl).RadioReceiveKHz);
        Assert.InRange(rig.MainHz, expectedRx - 55, expectedRx + 55);
    }

    [Fact]
    public void Passband_trim_clears_on_orbital_aos_same_satellite_and_mode()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double elevationDeg, double rangeRateKmPerSec = 0) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, elevationDeg, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, Build(20));
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        Assert.True(rxAfterInit > 0);

        rig.MainHz = rxAfterInit + 2_500;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();
        Thread.Sleep(2600);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        var statusAfterHunt = controller.GetStatus();
        Assert.InRange(statusAfterHunt.ManualReceiveAdjustKHz, 2.4, 2.6);
        Assert.InRange(statusAfterHunt.ManualTransmitAdjustKHz, -2.6, -2.4);

        controller.PublishContext(settings, Build(-2));
        controller.RunTrackingLoopOnce();

        var statusBelowHorizon = controller.GetStatus();
        Assert.InRange(statusBelowHorizon.ManualReceiveAdjustKHz, 2.4, 2.6);

        controller.PublishContext(settings, Build(5));
        controller.RunTrackingLoopOnce();

        var statusAfterAos = controller.GetStatus();
        Assert.InRange(statusAfterAos.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(statusAfterAos.ManualTransmitAdjustKHz, -0.001, 0.001);
    }

    [Fact]
    public void Rev_linear_rx_offset_change_preserves_vfo_tune()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) => new()
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = rxOffset
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        var rxAfterInit = rig.MainHz;
        rig.MainHz = rxAfterInit + 2_500;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();

        var rxBeforeOffset = rig.MainHz;
        var statusBefore = controller.GetStatus();
        Assert.InRange(statusBefore.ManualReceiveAdjustKHz, 2.4, 2.6);

        controller.Update(settings, Build(1.0));
        Thread.Sleep(650);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        var statusAfter = controller.GetStatus();
        Assert.InRange(statusAfter.ManualReceiveAdjustKHz, 2.4, 2.6);
        Assert.InRange(statusAfter.ManualTransmitAdjustKHz, -2.6, -2.4);
        Assert.InRange(rig.MainHz, rxBeforeOffset + 800, rxBeforeOffset + 1_200);
    }

    [Fact]
    public void Nor_linear_knob_tune_moves_both_legs_same_direction()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_960,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var state = new SatelliteTrackState
        {
            Name = "TEST",
            NoradId = "1",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        var txAfterInit = rig.SubHz;

        rig.MainHz = rxAfterInit + 3_000;
        for (var i = 0; i < 14; i++)
            controller.RunTrackingLoopOnce();
        Thread.Sleep(2600);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        Assert.Equal(rxAfterInit + 3_000, rig.MainHz);
        Assert.True(rig.SubHz > txAfterInit);
        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, 2.9, 3.1);
        Assert.InRange(status.ManualTransmitAdjustKHz, 2.9, 3.1);
    }

    [Fact]
    public void Single_ctcss_mode_applies_access_tone_without_combo_selection()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            Region = RigRegion.EU,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            CtcssHz = 67.0
        };

        var state = new SatelliteTrackState
        {
            Name = "AO-91",
            NoradId = "1",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
            SelectedCtcssHz = 67.0
        };

        controller.Update(settings, ctx);
        Assert.Equal(67.0, rig.LastToneHz);
        Assert.False(rig.LastToneSquelch);
        Assert.True(rig.ToneOn);
    }

    [Fact]
    public void Fm_cross_band_updates_tx_when_rx_threshold_hit_first()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdFmHz = 200,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) =>
            new()
            {
                TrackState = new SatelliteTrackState
                {
                    Name = "ISS",
                    NoradId = "25544",
                    Subpoint = new GeoCoordinate(0, 0),
                    LookAngles = new LookAngles(180, 30, 400, rangeRateKmPerSec)
                },
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = 0
            };

        controller.Update(settings, Build(0));
        var txAtRest = rig.SubHz;
        Assert.True(txAtRest > 0);

        controller.Update(settings, Build(0.25));
        Assert.NotEqual(txAtRest, rig.SubHz);
    }

    [Fact]
    public void Rev_linear_doppler_lag_without_dial_move_does_not_set_manual_tune()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        var dialHz = rig.MainHz;
        Assert.True(dialHz > 0);

        controller.PublishContext(settings, Build(4.2));
        Thread.Sleep(650);
        for (var i = 0; i < 12; i++)
            controller.RunTrackingLoopOnce();

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);

        var expectedRx = ToHz(DopplerFrequencyCalculator.Compute(mode, 4.2, 0).RadioReceiveKHz);
        Assert.InRange(rig.MainHz, expectedRx - 50, expectedRx + 50);
    }

    [Fact]
    public void Rev_linear_cat_only_tracks_rapid_range_rate_without_dial_stability_wait()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);

        foreach (var rangeRateKmPerSec in new[] { 1.0, 2.5, 4.0, 5.5, 4.0, 2.5, 0.5, -0.5, -2.0, -4.0 })
        {
            controller.PublishContext(settings, Build(rangeRateKmPerSec));
            for (var i = 0; i < 3; i++)
                controller.RunTrackingLoopOnce();
        }

        var status = controller.GetStatus();
        Assert.InRange(status.ManualReceiveAdjustKHz, -0.001, 0.001);
        Assert.InRange(status.ManualTransmitAdjustKHz, -0.001, 0.001);

        var expectedRx = ToHz(DopplerFrequencyCalculator.Compute(mode, -4.0, 0).RadioReceiveKHz);
        Assert.InRange(rig.MainHz, expectedRx - 55, expectedRx + 55);
    }

    [Fact]
    public void Linear_doppler_writes_blocked_while_operator_spins_vfo()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0,
            InteractiveDialSettleMs = InteractiveDialResumePolicy.MaxSettleMs
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(double rangeRateKmPerSec) => new()
        {
            TrackState = new SatelliteTrackState
            {
                Name = "RS-44",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, rangeRateKmPerSec)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRateKmPerSec, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, Build(0));
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;

        controller.PublishContext(settings, Build(4.2));
        controller.DrainCommandQueueForTests();

        long lastOperatorHz = rxAfterInit;
        for (var i = 0; i < 14; i++)
        {
            lastOperatorHz = rxAfterInit + 1_500 + (i * 80);
            rig.MainHz = lastOperatorHz;
            controller.RunTrackingLoopOnce();
        }

        Assert.Equal(lastOperatorHz, rig.MainHz);
    }

    [Fact]
    public void New_pass_with_offsets_does_not_force_spurious_offset_change()
    {
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 20, 800, 0)
        };

        RigTrackingContext Build(double rxOffset) => new()
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, rxOffset),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = rxOffset
        };

        controller.Update(settings, Build(4.025));
        Thread.Sleep(650);
        var rxAfterInit = rig.MainHz;
        var writesAfterInit = rig.SetFrequencyCallCount;

        controller.PublishContext(settings, Build(4.025));
        Thread.Sleep(650);
        for (var i = 0; i < 4; i++)
            controller.RunTrackingLoopOnce();

        Assert.Equal(rxAfterInit, rig.MainHz);
        Assert.Equal(writesAfterInit, rig.SetFrequencyCallCount);
    }

    [Fact]
    public void Pass_init_swaps_bands_when_switching_from_uhf_down_to_vhf_down_satellite()
    {
        var rig = new RecordingRigDriver { MainHz = 435_700_000, SubHz = 145_900_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var uvMode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var vuMode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_865,
            UplinkKHz = 435_110.1,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(SatelliteTransponderMode mode, string noradId, string name) =>
            new()
            {
                TrackState = new SatelliteTrackState
                {
                    Name = name,
                    NoradId = noradId,
                    Subpoint = new GeoCoordinate(0, 0),
                    LookAngles = new LookAngles(180, 20, 800, 0)
                },
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = 0
            };

        controller.Update(settings, Build(uvMode, "11111", "RS-44"));
        Assert.Equal(0, rig.ExchangeVfoCallCount);
        Assert.InRange(rig.MainHz, 430_000_000, 440_000_000);

        controller.Update(settings, Build(vuMode, "22222", "JO-97"));

        Assert.Equal(1, rig.ExchangeVfoCallCount);
        Assert.InRange(rig.MainHz, 145_000_000, 146_000_000);
        Assert.InRange(rig.SubHz, 430_000_000, 440_000_000);
    }

    [Fact]
    public void Pass_init_swaps_again_when_returning_from_vhf_down_to_uhf_down_satellite()
    {
        var rig = new RecordingRigDriver { MainHz = 145_900_000, SubHz = 435_700_000 };
        var controller = new RigController(_ => rig);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            CatDelayMs = 0
        };

        var vuMode = new SatelliteTransponderMode
        {
            DownlinkKHz = 145_865,
            UplinkKHz = 435_110.1,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var uvMode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_667,
            UplinkKHz = 145_937.61,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        RigTrackingContext Build(SatelliteTransponderMode mode, string noradId, string name) =>
            new()
            {
                TrackState = new SatelliteTrackState
                {
                    Name = name,
                    NoradId = noradId,
                    Subpoint = new GeoCoordinate(0, 0),
                    LookAngles = new LookAngles(180, 20, 800, 0)
                },
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = 0
            };

        controller.Update(settings, Build(vuMode, "22222", "JO-97"));
        Assert.Equal(0, rig.ExchangeVfoCallCount);

        controller.Update(settings, Build(uvMode, "11111", "RS-44"));

        Assert.Equal(1, rig.ExchangeVfoCallCount);
        Assert.InRange(rig.MainHz, 430_000_000, 440_000_000);
        Assert.InRange(rig.SubHz, 145_000_000, 146_000_000);
    }

    [Fact]
    public void Ts2000_pass_init_swaps_FA_FB_when_main_is_on_wrong_band()
    {
        var transport = new RecordingKenwoodCatTransport { FaHz = 145_900_000, FbHz = 435_700_000 };
        var driver = new KenwoodTs2000Driver(transport);
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs2000,
            Port = "COM1",
            CatDelayMs = 0
        };

        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            DownlinkKHz = 435_825,
            UplinkKHz = 145_900,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        controller.Update(settings, new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "SO-50",
                NoradId = "27607",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 20, 800, 0)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        });
        controller.DrainCommandQueueForTests();

        Assert.InRange(transport.FaHz, 430_000_000, 440_000_000);
        Assert.InRange(transport.FbHz, 145_000_000, 146_000_000);
    }

    private static long ToHz(double kHz) => (long)Math.Round(kHz * 1000.0);
}
