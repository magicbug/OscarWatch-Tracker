using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

[Collection(NetworkRadioCollection.Name)]
public class RigControllerFlexTests
{
    [Fact]
    public void Flex_cross_band_pass_enables_fdx_and_writes_rx_tx_frequencies()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 145_900,
            UplinkKHz = 435_800,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            CtcssHz = 67.0
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            selectedCtcssHz: 67.0,
            ready: () => stub.FullDuplexEnabled);

        Assert.True(harness.Driver!.IsConnected);
        Assert.True(harness.Driver.IsSatelliteModeActive);
        Assert.True(stub.FullDuplexEnabled);

        var status = harness.Controller.GetStatus();
        Assert.True(status.IsTracking);
        Assert.NotNull(status.LastReceiveHz);
        Assert.NotNull(status.LastTransmitHz);

        Assert.Contains(stub.Slices.Values, s => s.FrequencyHz is >= 145_000_000 and <= 146_000_000);
        Assert.Contains(stub.Slices.Values, s => s.FrequencyHz is >= 435_000_000 and <= 436_000_000);
        Assert.Contains(
            stub.Slices.Values,
            s => s.Tx && s.ToneMode.Equals("ctcss_tx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_cross_band_pass_applies_configured_antenna_ports()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 435_800,
            UplinkKHz = 145_900,
            DownlinkMode = "FM",
            UplinkMode = "FM"
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            settings: new RigSettings
            {
                Enabled = true,
                Type = RigType.FlexSmartSdr,
                NetworkHost = "127.0.0.1",
                NetworkPort = harness.Stub.Port,
                DopplerThresholdFmHz = 200,
                CatDelayMs = 50,
                FlexVhfRxAnt = "RX_B",
                FlexUhfRxAnt = "RX_A",
                FlexVhfTxAnt = "XVTR",
                FlexUhfTxAnt = "ANT1"
            },
            ready: () => stub.FullDuplexEnabled);

        var rxSlice = stub.Slices[harness.Driver!.RxSliceIndex];
        var txSlice = stub.Slices[harness.Driver.TxSliceIndex];
        Assert.Equal("RX_A", rxSlice.RxAnt);
        Assert.Equal("RX_B", txSlice.RxAnt);
        Assert.Equal("XVTR", txSlice.TxAnt);
    }

    [Fact]
    public void Flex_dual_hf_startup_pass_applies_both_slice_rxants()
    {
        using var stub = new FlexSmartSdrStubServer(dualHfPanStartup: true);
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "Linear",
            DownlinkKHz = 145_950,
            UplinkKHz = 432_146,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            settings: new RigSettings
            {
                Enabled = true,
                Type = RigType.FlexSmartSdr,
                NetworkHost = "127.0.0.1",
                NetworkPort = harness.Stub.Port,
                DopplerThresholdFmHz = 200,
                CatDelayMs = 50,
                FlexVhfRxAnt = "RX_B",
                FlexUhfRxAnt = "RX_A",
                FlexVhfTxAnt = "XVTR",
                FlexUhfTxAnt = "ANT1"
            },
            ready: () => stub.FullDuplexEnabled
                && stub.Slices.TryGetValue(harness.Driver!.RxSliceIndex, out var rx)
                && stub.Slices.TryGetValue(harness.Driver.TxSliceIndex, out var tx)
                && rx.RxAnt == "RX_B"
                && tx.RxAnt == "RX_A");

        var rxSlice = stub.Slices[harness.Driver!.RxSliceIndex];
        var txSlice = stub.Slices[harness.Driver.TxSliceIndex];
        Assert.Equal("RX_B", rxSlice.RxAnt);
        Assert.Equal("RX_A", txSlice.RxAnt);
        Assert.Equal("ANT1", txSlice.TxAnt);
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));
    }

    [Fact]
    public void Flex_fm_doppler_keeps_moving_rx_slice_after_status_echoes()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 435_400,
            UplinkKHz = 145_850,
            DownlinkMode = "FM",
            UplinkMode = "FM"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.FlexSmartSdr,
            NetworkHost = "127.0.0.1",
            NetworkPort = harness.Stub.Port,
            DopplerThresholdFmHz = 200,
            CatDelayMs = 50
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            settings: settings,
            satelliteName: "AO-123",
            ready: () => harness.Controller.GetStatus().IsTracking
                && harness.Driver?.IsSatelliteModeActive == true);

        var rxSlice = harness.Driver!.RxSliceIndex;
        var txSlice = harness.Driver.TxSliceIndex;
        var rxAfterInit = stub.Slices[rxSlice].FrequencyHz;
        var txAfterInit = stub.Slices[txSlice].FrequencyHz;

        for (var i = 1; i <= 8; i++)
        {
            var rangeRate = i * 0.8;
            var ctx = new RigTrackingContext
            {
                TrackState = new SatelliteTrackState
                {
                    Name = "AO-123",
                    NoradId = "25544",
                    Subpoint = new GeoCoordinate(0, 0),
                    LookAngles = new LookAngles(180, 25, 900, rangeRate)
                },
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, rangeRate, 0)
            };

            harness.Controller.PublishContext(settings, ctx);
            harness.Controller.RunTrackingLoopOnce();
        }

        Assert.NotEqual(rxAfterInit, stub.Slices[rxSlice].FrequencyHz);
        Assert.NotEqual(txAfterInit, stub.Slices[txSlice].FrequencyHz);
    }

    [Fact]
    public void Flex_linear_manual_rx_tune_is_not_snapped_back()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);
        var mode = new SatelliteTransponderMode
        {
            Type = "Linear",
            DownlinkKHz = 145_900,
            UplinkKHz = 435_800,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            ready: () => harness.Controller.GetStatus().IsTracking
                && harness.Driver?.IsSatelliteModeActive == true);

        var tunedHz = 145_905_000L;
        stub.SetSliceFrequencyFromOperator(harness.Driver!.RxSliceIndex, tunedHz);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var actual = stub.Slices[harness.Driver.RxSliceIndex].FrequencyHz;
            if (Math.Abs(actual - tunedHz) <= 50
                && harness.Controller.GetStatus().LastReceiveHz is { } displayed
                && Math.Abs(displayed - tunedHz) <= 50)
            {
                Thread.Sleep(300);
                break;
            }

            Thread.Sleep(20);
        }

        Assert.InRange(
            stub.Slices[harness.Driver.RxSliceIndex].FrequencyHz,
            tunedHz - 50,
            tunedHz + 50);
        Assert.InRange(
            harness.Controller.GetStatus().LastReceiveHz!.Value,
            tunedHz - 50,
            tunedHz + 50);
    }

    [Fact]
    public void Flex_fdx_failure_is_visible_in_rig_status_and_stops_tracking()
    {
        using var stub = new FlexSmartSdrStubServer(rejectFullDuplex: true);
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 145_900,
            UplinkKHz = 435_800,
            DownlinkMode = "FM",
            UplinkMode = "FM"
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            ready: () => harness.Controller.GetStatus().StatusKind == RigStatusKind.FlexControlFailed);

        var status = harness.Controller.GetStatus();
        Assert.True(status.IsConnected);
        Assert.False(status.IsTracking);
        Assert.Equal(RigStatusKind.FlexControlFailed, status.StatusKind);
        Assert.Contains("full duplex", status.StatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Flex_same_band_packet_enables_fdx_with_two_slices_not_classic_split()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "Packet",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        Assert.True(RigSatModeHelper.IsSameBandSimplex(mode.DownlinkKHz, mode.UplinkKHz));
        Assert.False(RigSatModeHelper.UseMainSubLayout(mode.DownlinkKHz, mode.UplinkKHz));

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 2.5, 0),
            ready: () => stub.FullDuplexEnabled && harness.Driver?.IsSatelliteModeActive == true);

        Assert.True(stub.FullDuplexEnabled);
        Assert.True(harness.Driver!.IsSatelliteModeActive);

        // Same-band duplex still uses two slices (Main RX / Sub TX), not ICOM-style VFO A/B split.
        var vhfSlices = stub.Slices.Values
            .Where(s => s.FrequencyHz is >= 145_000_000 and <= 146_000_000)
            .ToList();
        Assert.True(vhfSlices.Count >= 2, $"Expected two VHF slices, got {vhfSlices.Count}");
        Assert.Contains(stub.Slices.Values, s => s.Tx);

        var status = harness.Controller.GetStatus();
        Assert.True(status.IsTracking);
        Assert.NotNull(status.LastReceiveHz);
        Assert.NotNull(status.LastTransmitHz);
        Assert.InRange(status.LastReceiveHz!.Value, 145_000_000, 146_000_000);
        Assert.InRange(status.LastTransmitHz!.Value, 145_000_000, 146_000_000);
    }

    [Fact]
    public void Flex_beacon_only_disables_fdx_and_tracks_downlink_only()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "SSTV (UHF)",
            DownlinkKHz = 437_550,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        Assert.True(mode.IsBeaconOnly);

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 20),
            ready: () => harness.Controller.GetStatus().IsTracking
                && harness.Driver?.IsConnected == true
                && !stub.FullDuplexEnabled);

        Assert.False(stub.FullDuplexEnabled);
        Assert.False(harness.Driver!.IsSatelliteModeActive);

        var status = harness.Controller.GetStatus();
        Assert.True(status.IsTracking);
        Assert.NotNull(status.LastReceiveHz);
        Assert.InRange(status.LastReceiveHz!.Value, 437_000_000, 438_000_000);
        Assert.Null(status.LastTransmitHz);

        Assert.Contains(stub.Slices.Values, s => s.FrequencyHz is >= 437_000_000 and <= 438_000_000);
        Assert.DoesNotContain(
            stub.Slices.Values,
            s => s.ToneMode.Equals("ctcss_tx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_ssb_pass_sets_usb_lsb_and_skips_ctcss()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_850,
            UplinkKHz = 145_952,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        PublishAndWait(
            harness,
            mode,
            DopplerFrequencyCalculator.Compute(mode, 0, 0),
            selectedCtcssHz: 67.0,
            ready: () => stub.FullDuplexEnabled);

        var rxSlice = stub.Slices.Values.First(s => !s.Tx);
        var txSlice = stub.Slices.Values.First(s => s.Tx);
        Assert.Equal("USB", rxSlice.Mode);
        Assert.Equal("LSB", txSlice.Mode);
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Contains("fm_tone_mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_vu_to_uv_swap_binds_slices_before_tune()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var fo29 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_850,
            UplinkKHz = 145_952,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            fo29,
            DopplerFrequencyCalculator.Compute(fo29, 0, 0),
            ready: () => stub.FullDuplexEnabled);

        stub.ClearCommandBodies();

        var ao07 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 145_950,
            UplinkKHz = 432_146,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            ao07,
            DopplerFrequencyCalculator.Compute(ao07, 0, 0),
            ready: () => harness.Controller.GetStatus().IsTracking);

        var bodies = stub.CommandBodies.ToList();
        var firstRemove = bodies.FindIndex(b =>
            b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        var firstCreate = bodies.FindIndex(b =>
            b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
            && b.Contains(" pan=", StringComparison.OrdinalIgnoreCase));
        var firstTune = bodies.FindIndex(b => b.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase));

        Assert.True(firstRemove >= 0, "Expected slice remove during AO-07 pan rebind");
        Assert.True(firstCreate > firstRemove, "Expected slice create on locked pans after remove");
        Assert.True(firstTune > firstCreate, "Expected pan rebind before tune on V/U to U/V swap");

        var rxSlice = stub.Slices.Values.First(s => !s.Tx);
        var txSlice = stub.Slices.Values.First(s => s.Tx);
        Assert.Equal("USB", rxSlice.Mode);
        Assert.Equal("LSB", txSlice.Mode);
        Assert.Equal("0x40000001", rxSlice.PanStreamId);
        Assert.Equal("0x40000000", txSlice.PanStreamId);
    }

    [Fact]
    public void Flex_uv_after_vu_layout_flip_force_rebinds()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var jo97 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 145_865,
            UplinkKHz = 435_110.1,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            jo97,
            DopplerFrequencyCalculator.Compute(jo97, 0, 0),
            ready: () => stub.FullDuplexEnabled);

        stub.ClearCommandBodies();

        var rs44 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_640,
            UplinkKHz = 145_965,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            rs44,
            DopplerFrequencyCalculator.Compute(rs44, 0, 0),
            ready: () => harness.Controller.GetStatus().IsTracking);

        var bodies = stub.CommandBodies.ToList();
        Assert.Contains(bodies, b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            bodies,
            b => b.Contains("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" pan=0x40000000", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            bodies,
            b => b.Contains("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" pan=0x40000001", StringComparison.OrdinalIgnoreCase));

        var rxSlice = stub.Slices.Values.First(s => !s.Tx);
        var txSlice = stub.Slices.Values.First(s => s.Tx);
        Assert.Equal("0x40000000", rxSlice.PanStreamId);
        Assert.Equal("0x40000001", txSlice.PanStreamId);
    }

    [Fact]
    public void Flex_same_uv_layout_satellite_change_skips_rebind_when_already_healthy()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var iss = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        PublishAndWait(
            harness,
            iss,
            DopplerFrequencyCalculator.Compute(iss, 0, 0),
            selectedCtcssHz: 67.0,
            satelliteName: "ISS",
            ready: () => stub.FullDuplexEnabled);

        stub.ClearCommandBodies();

        var rs44 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_640,
            UplinkKHz = 145_965,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            rs44,
            DopplerFrequencyCalculator.Compute(rs44, 0, 0),
            satelliteName: "RS-44",
            ready: () => harness.Controller.GetStatus().IsTracking);

        // Same-layout sat change must not tear down a healthy dual-band bind (Mark single-pan lock-up).
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));

        var rxSlice = stub.Slices.Values.First(s => !s.Tx);
        var txSlice = stub.Slices.Values.First(s => s.Tx);
        Assert.Equal("0x40000000", rxSlice.PanStreamId);
        Assert.Equal("0x40000001", txSlice.PanStreamId);
        Assert.Equal("USB", rxSlice.Mode);
        Assert.Equal("LSB", txSlice.Mode);
    }

    [Fact]
    public void Flex_single_pan_start_survives_vu_flip_then_same_layout_sat_change()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1, silentCrossScuCenterReject: true);
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var ao73 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 145_960,
            UplinkKHz = 435_148,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            ao73,
            DopplerFrequencyCalculator.Compute(ao73, 0, 0),
            satelliteName: "AO-73",
            ready: () => stub.FullDuplexEnabled);

        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));

        var iss = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 437_800,
            UplinkKHz = 145_990,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR",
            CtcssHz = 67.0
        };

        PublishAndWait(
            harness,
            iss,
            DopplerFrequencyCalculator.Compute(iss, 0, 0),
            selectedCtcssHz: 67.0,
            satelliteName: "ISS",
            ready: () => harness.Controller.GetStatus().IsTracking);

        stub.ClearCommandBodies();

        var fo29 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            fo29,
            DopplerFrequencyCalculator.Compute(fo29, 0, 0),
            satelliteName: "FO-29",
            ready: () => harness.Controller.GetStatus().IsTracking);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));

        var rxSlice = stub.Slices.Values.First(s => !s.Tx);
        var txSlice = stub.Slices.Values.First(s => s.Tx);
        Assert.NotEqual(rxSlice.PanStreamId, txSlice.PanStreamId, StringComparer.OrdinalIgnoreCase);
        Assert.True(RigSatModeHelper.IsUhfCenterKHz(stub.PanCentersHz[rxSlice.PanStreamId] / 1000.0));
        Assert.True(RigSatModeHelper.IsVhfCenterKHz(stub.PanCentersHz[txSlice.PanStreamId] / 1000.0));
        Assert.Equal(2, stub.Slices.Count);
    }

    [Fact]
    public void Flex_same_satellite_reinit_skips_rebind_when_already_bound()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var rs44 = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435_640,
            UplinkKHz = 145_965,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            rs44,
            DopplerFrequencyCalculator.Compute(rs44, 0, 0),
            satelliteName: "RS-44",
            ready: () => stub.FullDuplexEnabled);

        stub.ClearCommandBodies();

        PublishAndWait(
            harness,
            rs44,
            DopplerFrequencyCalculator.Compute(rs44, 0, 0),
            satelliteName: "RS-44",
            ready: () => harness.Controller.GetStatus().IsTracking);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_beacon_after_same_band_pass_turns_fdx_off()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var packet = new SatelliteTransponderMode
        {
            Type = "Packet",
            DownlinkKHz = 145_825,
            UplinkKHz = 145_825,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            packet,
            DopplerFrequencyCalculator.Compute(packet, 0, 20),
            ready: () => stub.FullDuplexEnabled);
        Assert.True(stub.FullDuplexEnabled);

        var beacon = new SatelliteTransponderMode
        {
            Type = "SSTV (UHF)",
            DownlinkKHz = 437_550,
            UplinkKHz = 0,
            DownlinkMode = "FM",
            UplinkMode = "FM",
            Doppler = "NOR"
        };

        PublishAndWait(
            harness,
            beacon,
            DopplerFrequencyCalculator.Compute(beacon, 0, 20),
            ready: () => !stub.FullDuplexEnabled && harness.Driver?.IsSatelliteModeActive == false);

        Assert.False(stub.FullDuplexEnabled);
        Assert.False(harness.Driver!.IsSatelliteModeActive);

        var status = harness.Controller.GetStatus();
        Assert.True(status.IsTracking);
        Assert.NotNull(status.LastReceiveHz);
        Assert.Null(status.LastTransmitHz);
    }

    [Fact]
    public void DisconnectAndWait_aborts_in_flight_flex_pass_init()
    {
        using var stub = new FlexSmartSdrStubServer(
            initialSliceCount: 1,
            stickyPanCenterUntilAutopan: true);
        stub.WaitUntilReady();
        using var harness = CreateHarness(stub);

        var mode = new SatelliteTransponderMode
        {
            Type = "FM VOICE",
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };

        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.FlexSmartSdr,
            NetworkHost = "127.0.0.1",
            NetworkPort = harness.Stub.Port,
            DopplerThresholdFmHz = 200,
            CatDelayMs = 50
        };

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = "FO-29",
                NoradId = "24278",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 25, 900, 1.5)
            },
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0)
        };

        var publishStarted = new ManualResetEventSlim(false);
        var publishTask = Task.Run(() =>
        {
            publishStarted.Set();
            harness.Controller.PublishContext(settings, ctx, reinitializePass: true);
            harness.Controller.DrainCommandQueueForTests();
        });

        Assert.True(publishStarted.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        harness.Controller.DisconnectAndWait();
        sw.Stop();

        publishTask.Wait(TimeSpan.FromSeconds(5));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(harness.Driver?.IsConnected ?? true);
    }

    private static FlexHarness CreateHarness(FlexSmartSdrStubServer stub)
    {
        FlexRadioDriver? driver = null;
        var controller = new RigController(settings =>
        {
            driver = new FlexRadioDriver(settings.NetworkHost, settings.NetworkPort, settings.CatDelayMs);
            return driver;
        });

        return new FlexHarness(controller, stub, () => driver);
    }

    private static void PublishAndWait(
        FlexHarness harness,
        SatelliteTransponderMode mode,
        CorrectedFrequencies corrected,
        Func<bool> ready,
        double? selectedCtcssHz = null,
        RigSettings? settings = null,
        string? satelliteName = null)
    {
        settings ??= new RigSettings
        {
            Enabled = true,
            Type = RigType.FlexSmartSdr,
            NetworkHost = "127.0.0.1",
            NetworkPort = harness.Stub.Port,
            DopplerThresholdFmHz = 200,
            CatDelayMs = 50
        };

        settings.Enabled = true;
        settings.Type = RigType.FlexSmartSdr;
        settings.NetworkHost = "127.0.0.1";
        settings.NetworkPort = harness.Stub.Port;

        var ctx = new RigTrackingContext
        {
            TrackState = new SatelliteTrackState
            {
                Name = string.IsNullOrWhiteSpace(satelliteName) ? mode.Type : satelliteName.Trim(),
                NoradId = "25544",
                Subpoint = new GeoCoordinate(0, 0),
                LookAngles = new LookAngles(180, 25, 900, 1.5)
            },
            Mode = mode,
            Corrected = corrected,
            SelectedCtcssHz = selectedCtcssHz
        };

        harness.Controller.PublishContext(settings, ctx);
        harness.Controller.DrainCommandQueueForTests();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var status = harness.Controller.GetStatus();
            if (status.IsConnected && ready())
                return;
            Thread.Sleep(50);
        }

        var finalStatus = harness.Controller.GetStatus();
        throw new TimeoutException(
            $"Flex controller did not reach the expected state. Status={finalStatus.StatusKind}, detail={finalStatus.StatusDetail}");
    }

    private sealed class FlexHarness : IDisposable
    {
        private readonly Func<FlexRadioDriver?> _driverAccessor;

        public FlexHarness(RigController controller, FlexSmartSdrStubServer stub, Func<FlexRadioDriver?> driverAccessor)
        {
            Controller = controller;
            Stub = stub;
            _driverAccessor = driverAccessor;
        }

        public RigController Controller { get; }
        public FlexSmartSdrStubServer Stub { get; }
        public FlexRadioDriver? Driver => _driverAccessor();

        public void Dispose() => Controller.Dispose();
    }
}
