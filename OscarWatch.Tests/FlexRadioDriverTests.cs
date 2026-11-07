using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

[Collection(NetworkRadioCollection.Name)]
public class FlexRadioDriverTests
{
    [Fact]
    public void Open_ConnectsAndCachesSlices()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();

        Assert.True(driver.IsConnected);
        Assert.Equal(RigType.FlexSmartSdr, driver.RigType);

        var rx = driver.ReadFrequencyHz(RigVfo.Main);
        var tx = driver.ReadFrequencyHz(RigVfo.Sub);
        Assert.Equal(145_900_000, rx);
        Assert.Equal(435_000_000, tx);
    }

    [Fact]
    public void Open_WhenOptionalClientLabelIsRejected_StillConnects()
    {
        using var stub = new FlexSmartSdrStubServer(rejectClientProgram: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();

        Assert.True(driver.IsConnected);
        Assert.Equal(145_900_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Equal(435_000_000, driver.ReadFrequencyHz(RigVfo.Sub));
    }

    [Fact]
    public void SetSatelliteMode_EnablesFullDuplexAndTxSlice()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.True(stub.FullDuplexEnabled);
        Assert.Contains(stub.Slices.Values, s => s.Tx);
    }

    [Fact]
    public void SetSatelliteMode_WhenFullDuplexRejected_ThrowsAndRemainsInactive()
    {
        using var stub = new FlexSmartSdrStubServer(rejectFullDuplex: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();

        var ex = Assert.Throws<FlexSatelliteSetupException>(() => driver.SetSatelliteMode(true));

        Assert.Contains("full duplex", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(driver.IsSatelliteModeActive);
        Assert.False(stub.FullDuplexEnabled);
    }

    [Fact]
    public void SetSatelliteMode_WhenSecondSliceCannotBeCreated_ThrowsAndDisablesFullDuplex()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1, rejectSliceCreate: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();

        var ex = Assert.Throws<FlexSatelliteSetupException>(() => driver.SetSatelliteMode(true));

        Assert.Contains("separate RX and TX slices", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(driver.IsSatelliteModeActive);
        Assert.False(stub.FullDuplexEnabled);
    }

    [Fact]
    public void SetSatelliteMode_RetriesWhenInitialSliceCreatesFailTransiently()
    {
        // Zero slices + first two create attempts rejected (one EnsureDuplex pass), then creates succeed.
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 0, rejectSliceCreateCount: 2);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.True(stub.FullDuplexEnabled);
        Assert.NotEqual(driver.RxSliceIndex, driver.TxSliceIndex);
        Assert.Equal(2, stub.Slices.Count);
    }

    [Fact]
    public void SetSatelliteMode_ConfirmsCreatedSliceFromStatusWhenResponseOmitsIndex()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1, omitSliceCreateIndex: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.NotEqual(driver.RxSliceIndex, driver.TxSliceIndex);
        Assert.Equal(2, stub.Slices.Count);
        Assert.True(stub.Slices[driver.TxSliceIndex].Tx);
    }

    [Fact]
    public void SetSatelliteMode_AcceptsCreateIndexWhenStatusIsDelayed()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1, omitSliceCreateStatus: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.NotEqual(driver.RxSliceIndex, driver.TxSliceIndex);
        Assert.Equal(2, stub.Slices.Count);
        Assert.True(stub.Slices[driver.TxSliceIndex].Tx);
    }

    [Fact]
    public void SetSatelliteMode_IgnoresPartialStatusGhostSlicesWithoutInUse()
    {
        using var stub = new FlexSmartSdrStubServer(
            initialSliceCount: 1,
            emitGhostPartialSliceOnSubscribe: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        stub.ClearCommandBodies();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stub.Slices.Keys, index => index == 9);
        Assert.Equal(2, stub.Slices.Count);
        Assert.NotEqual(driver.RxSliceIndex, driver.TxSliceIndex);
    }

    [Fact]
    public void SetSatelliteMode_CreatesSecondSliceWithoutAntParameter()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.ConfigureAntennaPorts(new RigSettings
        {
            FlexVhfRxAnt = "RX_B",
            FlexUhfRxAnt = "RX_A",
            FlexVhfTxAnt = "XVTR",
            FlexUhfTxAnt = "ANT1"
        });
        stub.ClearCommandBodies();
        driver.SetSatelliteMode(true);

        var creates = stub.CommandBodies
            .Where(b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(creates);
        Assert.All(creates, b => Assert.DoesNotContain("ant=", b, StringComparison.OrdinalIgnoreCase));
        Assert.True(driver.IsSatelliteModeActive);
    }

    [Fact]
    public void SetSatelliteMode_FromSinglePan_CreatesOppositeBandPanThenPeerSlice()
    {
        using var stub = new FlexSmartSdrStubServer(initialSliceCount: 1, silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        stub.ClearCommandBodies();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        // UHF SCU pan is allocated via a temporary slice (panafall create stays on VHF-group/HF).
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("freq=435", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" pan=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));

        var txPan = stub.Slices[driver.TxSliceIndex].PanStreamId;
        Assert.False(string.IsNullOrWhiteSpace(txPan));
        Assert.True(RigSatModeHelper.IsUhfCenterKHz(stub.PanCentersHz[txPan!] / 1000.0));
    }

    [Fact]
    public void CenterBandPanadapters_relocks_and_retries_when_sticky_ids_lag_live_centres()
    {
        using var stub = new FlexSmartSdrStubServer(silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000, forceRebind: true);
        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        // Invert live centres while leaving sticky locks pointing at the old IDs.
        stub.SwapPanBandCenters();
        stub.ClearCommandBodies();

        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        var centres = stub.PanCentersHz;
        Assert.Contains(centres, kv => RigSatModeHelper.IsVhfCenterKHz(kv.Value / 1000.0));
        Assert.Contains(centres, kv => RigSatModeHelper.IsUhfCenterKHz(kv.Value / 1000.0));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Equals("display panafall create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetSatelliteMode_WhenTxSliceRejected_ThrowsAndDisablesFullDuplex()
    {
        using var stub = new FlexSmartSdrStubServer(rejectTxSlice: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();

        var ex = Assert.Throws<FlexSatelliteSetupException>(() => driver.SetSatelliteMode(true));

        Assert.Contains("TX slice", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(driver.IsSatelliteModeActive);
        Assert.False(stub.FullDuplexEnabled);
    }

    [Fact]
    public void SetFrequencyHz_TunesCurrentVfoSlice()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.SelectVfo(RigVfo.Main);
        Assert.True(driver.SetFrequencyHz(145_800_000));
        Assert.Equal(145_800_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);

        driver.SelectVfo(RigVfo.Sub);
        Assert.True(driver.SetFrequencyHz(435_100_000));
        Assert.Equal(435_100_000, stub.Slices[driver.TxSliceIndex].FrequencyHz);
    }

    [Fact]
    public void ReadFrequencyHz_DrainsUnsolicitedManualSliceStatus()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.SetSliceFrequencyFromOperator(driver.RxSliceIndex, 145_850_000);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        long? observed = null;
        while (DateTime.UtcNow < deadline && observed != 145_850_000)
        {
            observed = driver.ReadFrequencyHz(RigVfo.Main);
            Thread.Sleep(10);
        }

        Assert.Equal(145_850_000, observed);
    }

    [Fact]
    public void SetFrequencyHz_RejectsStaleRxTuneWhenNewerStatusArrives()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        Assert.Equal(145_900_000, driver.ReadFrequencyHz(RigVfo.Main));

        stub.SetSliceFrequencyFromOperator(driver.RxSliceIndex, 145_850_000);
        Thread.Sleep(50);

        driver.SelectVfo(RigVfo.Main);
        Assert.False(driver.SetFrequencyHz(145_901_000));
        Assert.Equal(145_850_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);
        Assert.Equal(145_850_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void SetFrequencyHz_AfterSuccessfulRxTune_AppliesNextDopplerStepWithoutReread()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        Assert.Equal(145_900_000, driver.ReadFrequencyHz(RigVfo.Main));

        driver.SelectVfo(RigVfo.Main);
        Assert.True(driver.SetFrequencyHz(145_901_000));
        Assert.Equal(145_901_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);

        // SmartSDR echoes RF_frequency after our tune. FM automatic tracking does not read Main
        // again before the next Doppler step, so the compare-and-swap must accept our own echo.
        Thread.Sleep(50);
        Assert.True(driver.SetFrequencyHz(145_902_500));
        Assert.Equal(145_902_500, stub.Slices[driver.RxSliceIndex].FrequencyHz);
    }

    [Fact]
    public void SetMode_AndTone_ApplyToSlices()
    {
        using var stub = new FlexSmartSdrStubServer(emitPartialSliceStatus: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.SelectVfo(RigVfo.Main);
        driver.SetMode("FMN");
        Assert.Equal("NFM", stub.Slices[driver.RxSliceIndex].Mode);

        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("FM");
        driver.SetToneHz(67.0, squelchTone: false);
        driver.SetToneOn(true);

        var tx = stub.Slices[driver.TxSliceIndex];
        Assert.Equal("FM", tx.Mode);
        Assert.Equal("ctcss_tx", tx.ToneMode);
        Assert.Equal(67.0, tx.ToneHz);
        Assert.Equal(145_900_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Equal(435_000_000, driver.ReadFrequencyHz(RigVfo.Sub));
    }

    [Fact]
    public void SetTone_WhenRadioDoesNotEchoOwnStatus_SendsModeAndValue()
    {
        using var stub = new FlexSmartSdrStubServer(suppressSliceSetStatus: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("FM");
        driver.SetToneHz(74.4, squelchTone: false);
        driver.SetToneOn(true);

        var tx = stub.Slices[driver.TxSliceIndex];
        Assert.Equal("ctcss_tx", tx.ToneMode);
        Assert.Equal(74.4, tx.ToneHz);
        Assert.Equal(435_000_000, driver.ReadFrequencyHz(RigVfo.Sub));
    }

    [Fact]
    public void ApplyBandAntennaPorts_sets_rx_and_tx_ports_for_duplex_layout()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        var settings = new RigSettings
        {
            FlexVhfRxAnt = "RX_B",
            FlexUhfRxAnt = "RX_A",
            FlexVhfTxAnt = "XVTR",
            FlexUhfTxAnt = "ANT1"
        };

        stub.ClearCommandBodies();
        // U/V: downlink UHF → RX_A on RX slice; uplink VHF → RX_B on TX slice + XVTR txant
        driver.ApplyBandAntennaPorts(settings, 435_300_000, 145_800_000);

        var rx = stub.Slices[driver.RxSliceIndex];
        var tx = stub.Slices[driver.TxSliceIndex];
        Assert.Equal("RX_A", rx.RxAnt);
        Assert.Equal("RX_B", tx.RxAnt);
        Assert.Equal("XVTR", tx.TxAnt);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 0 rxant=RX_A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 rxant=RX_B", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 txant=XVTR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyBandAntennaPorts_sets_both_slice_rxants_for_vhf_downlink_layout()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        var settings = new RigSettings
        {
            FlexVhfRxAnt = "RX_B",
            FlexUhfRxAnt = "RX_A",
            FlexVhfTxAnt = "XVTR",
            FlexUhfTxAnt = "ANT1"
        };

        stub.ClearCommandBodies();
        // V/U: downlink VHF → RX_B; uplink UHF → RX_A + ANT1 txant
        driver.ApplyBandAntennaPorts(settings, 145_900_000, 435_300_000);

        var rx = stub.Slices[driver.RxSliceIndex];
        var tx = stub.Slices[driver.TxSliceIndex];
        Assert.Equal("RX_B", rx.RxAnt);
        Assert.Equal("RX_A", tx.RxAnt);
        Assert.Equal("ANT1", tx.TxAnt);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 0 rxant=RX_B", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 rxant=RX_A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 txant=ANT1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyBandAntennaPorts_with_empty_settings_sends_no_antenna_commands()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.ApplyBandAntennaPorts(new RigSettings(), 435_300_000, 145_800_000);

        foreach (var slice in stub.Slices.Values)
        {
            Assert.Equal("", slice.RxAnt);
            Assert.Equal("", slice.TxAnt);
        }
    }

    [Fact]
    public void CenterBandPanadapters_centres_vhf_and_uhf_band_pans()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.ClearCommandBodies();
        driver.CenterBandPanadapters(145_960_000, 435_148_000);

        Assert.Equal(145_960_000, stub.PanCentersHz["0x40000001"]);
        Assert.Equal(435_148_000, stub.PanCentersHz["0x40000000"]);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000001 center=145.96 autocenter=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000000 center=435.148 autocenter=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CenterBandPanadapters_vu_layout_centres_uhf_and_vhf_band_pans()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.ClearCommandBodies();
        driver.CenterBandPanadapters(435_863_000, 145_943_000);

        Assert.Equal(435_863_000, stub.PanCentersHz["0x40000000"]);
        Assert.Equal(145_943_000, stub.PanCentersHz["0x40000001"]);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000000 center=435.863 autocenter=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000001 center=145.943 autocenter=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BindDuplexSlicesToBandPans_vu_layout_binds_rx_uhf_tx_vhf()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(435_863_000, 145_943_000);

        Assert.Equal("0x40000000", stub.Slices[driver.RxSliceIndex].PanStreamId);
        Assert.Equal("0x40000001", stub.Slices[driver.TxSliceIndex].PanStreamId);
    }

    [Fact]
    public void Deferred_modes_set_usb_on_rx_and_lsb_on_tx()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(145_950_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(432_146_000);
        driver.CenterBandPanadapters(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetMode("USB");
        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("LSB");

        Assert.Equal("USB", stub.Slices[driver.RxSliceIndex].Mode);
        Assert.Equal("LSB", stub.Slices[driver.TxSliceIndex].Mode);
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_retunes_when_rx_slice_wrong()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(145_950_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(432_146_000);
        driver.CenterBandPanadapters(145_950_000, 432_146_000);

        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(145_867_000);
        Assert.Equal(145_867_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);

        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(145_950_000, 432_146_000);

        Assert.Equal(145_950_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);
        Assert.Equal(432_146_000, stub.Slices[driver.TxSliceIndex].FrequencyHz);
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" 145.95", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_noop_when_slices_already_correct()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(435_640_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(145_965_000);
        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(435_640_000, 145_965_000);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(435_640_000, stub.Slices[driver.RxSliceIndex].FrequencyHz);
        Assert.Equal(145_965_000, stub.Slices[driver.TxSliceIndex].FrequencyHz);
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_repairs_wrong_rx_mode()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(145_950_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(432_146_000);
        driver.CenterBandPanadapters(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetMode("FM");
        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("LSB");

        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(145_950_000, 432_146_000, "USB", "LSB");

        Assert.Equal("USB", stub.Slices[driver.RxSliceIndex].Mode);
        Assert.Equal("LSB", stub.Slices[driver.TxSliceIndex].Mode);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Contains("slice set ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" mode=USB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDualBandPanLayout_recovers_when_both_pans_collapsed_onto_uhf()
    {
        using var stub = new FlexSmartSdrStubServer(silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.CollapseBothPansOntoBand(ontoUhf: true);
        stub.ClearCommandBodies();

        Assert.True(driver.EnsureDualBandPanLayout(435_640_000, 145_965_000));

        var centres = stub.PanCentersHz;
        Assert.Contains(centres, kv => RigSatModeHelper.IsVhfCenterKHz(kv.Value / 1000.0));
        Assert.Contains(centres, kv => RigSatModeHelper.IsUhfCenterKHz(kv.Value / 1000.0));
        Assert.True(
            stub.CommandBodies.Any(b =>
                b.Equals("display panafall create", StringComparison.OrdinalIgnoreCase)
                || (b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                    && b.Contains("freq=145.965", StringComparison.OrdinalIgnoreCase))),
            "Expected temporary VHF slice allocate or panafall create during recovery");
    }

    [Fact]
    public void EnsureDualBandPanLayout_then_bind_places_slices_on_separate_band_pans()
    {
        using var stub = new FlexSmartSdrStubServer(silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.CollapseBothPansOntoBand(ontoUhf: true);

        Assert.True(driver.EnsureDualBandPanLayout(435_640_000, 145_965_000));
        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000, forceRebind: true);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId;
        var txPan = stub.Slices[driver.TxSliceIndex].PanStreamId;
        Assert.False(string.IsNullOrWhiteSpace(rxPan));
        Assert.False(string.IsNullOrWhiteSpace(txPan));
        Assert.NotEqual(rxPan, txPan, StringComparer.OrdinalIgnoreCase);

        Assert.True(RigSatModeHelper.IsUhfCenterKHz(stub.PanCentersHz[rxPan!] / 1000.0));
        Assert.True(RigSatModeHelper.IsVhfCenterKHz(stub.PanCentersHz[txPan!] / 1000.0));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_recovers_collapsed_pans_instead_of_preserving_bad_locks()
    {
        using var stub = new FlexSmartSdrStubServer(silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        // Healthy bind first so sticky locks exist, then collapse (Mark's death spiral).
        driver.EnsureDualBandPanLayout(435_640_000, 145_965_000);
        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000, forceRebind: true);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(435_640_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(145_965_000);
        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        stub.CollapseBothPansOntoBand(ontoUhf: true);
        stub.ClearCommandBodies();

        driver.EnsureDuplexPassFrequencies(435_640_000, 145_965_000, "USB", "USB");

        Assert.True(
            stub.CommandBodies.Any(b =>
                b.Equals("display panafall create", StringComparison.OrdinalIgnoreCase)
                || (b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                    && b.Contains("freq=145.965", StringComparison.OrdinalIgnoreCase))),
            "Expected temporary VHF slice allocate or panafall create during recovery");

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId;
        var txPan = stub.Slices[driver.TxSliceIndex].PanStreamId;
        Assert.NotEqual(rxPan, txPan, StringComparer.OrdinalIgnoreCase);
        Assert.True(RigSatModeHelper.IsUhfCenterKHz(stub.PanCentersHz[rxPan!] / 1000.0));
        Assert.True(RigSatModeHelper.IsVhfCenterKHz(stub.PanCentersHz[txPan!] / 1000.0));
    }

    [Fact]
    public void CenterBandPanadapters_does_not_claim_success_when_radio_silently_rejects_cross_band()
    {
        using var stub = new FlexSmartSdrStubServer(silentCrossScuCenterReject: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        stub.CollapseBothPansOntoBand(ontoUhf: true);
        // Centre failure now restores dual-band pans (temp-slice / panafall) then retries.
        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));
    }

    [Fact]
    public void EnsureDualBandPanLayout_recovers_from_dual_hf_pans_via_remove_then_temp_slice()
    {
        using var stub = new FlexSmartSdrStubServer(dualHfPanStartup: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        Assert.True(driver.EnsureDualBandPanLayout(145_950_000, 432_146_000));

        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("display pan remove ", StringComparison.OrdinalIgnoreCase)
                 || b.StartsWith("display panafall remove ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("freq=432.146", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Contains("display pan set ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("center=432.146", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CenterBandPanadapters_refuses_cross_scu_centre_when_sticky_locks_inverted()
    {
        using var stub = new FlexSmartSdrStubServer(
            silentCrossScuCenterReject: true,
            allowUhfToVhfCenter: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000, forceRebind: true);
        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        // Invert live centres while sticky locks still point at the old stream IDs.
        // Without client-side refuse, UHF→VHF centre would collapse both pans onto VHF-group.
        stub.SwapPanBandCenters();
        stub.ClearCommandBodies();

        driver.CenterBandPanadapters(435_640_000, 145_965_000);

        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));
        // With allowUhfToVhfCenter, a naive centre on inverted locks would collapse UHF onto VHF.
        Assert.Equal(1, stub.PanCentersHz.Values.Count(hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0)));
    }

    [Fact]
    public void Ao07_then_iss_after_dual_hf_recovery_keeps_separate_band_pans()
    {
        using var stub = new FlexSmartSdrStubServer(dualHfPanStartup: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        // AO-07 V/U
        Assert.True(driver.EnsureDualBandPanLayout(145_950_000, 432_146_000));
        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000, forceRebind: true);
        driver.CenterBandPanadapters(145_950_000, 432_146_000);

        // ISS U/V layout flip
        Assert.True(driver.EnsureDualBandPanLayout(437_800_000, 145_990_000));
        driver.BindDuplexSlicesToBandPans(437_800_000, 145_990_000, forceRebind: true);
        driver.CenterBandPanadapters(437_800_000, 145_990_000);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId;
        var txPan = stub.Slices[driver.TxSliceIndex].PanStreamId;
        Assert.False(string.IsNullOrWhiteSpace(rxPan));
        Assert.False(string.IsNullOrWhiteSpace(txPan));
        Assert.NotEqual(rxPan, txPan, StringComparer.OrdinalIgnoreCase);
        Assert.True(RigSatModeHelper.IsUhfCenterKHz(stub.PanCentersHz[rxPan!] / 1000.0));
        Assert.True(RigSatModeHelper.IsVhfCenterKHz(stub.PanCentersHz[txPan!] / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsVhfCenterKHz(hz / 1000.0));
        Assert.Contains(stub.PanCentersHz.Values, hz => RigSatModeHelper.IsUhfCenterKHz(hz / 1000.0));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_after_dual_hf_rebind_reapplies_both_rxants()
    {
        using var stub = new FlexSmartSdrStubServer(dualHfPanStartup: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        var settings = new RigSettings
        {
            FlexVhfRxAnt = "RX_B",
            FlexUhfRxAnt = "RX_A",
            FlexVhfTxAnt = "XVTR",
            FlexUhfTxAnt = "ANT1"
        };
        driver.ConfigureAntennaPorts(settings);

        const long downlinkHz = 145_950_000;
        const long uplinkHz = 432_146_000;
        Assert.True(driver.EnsureDualBandPanLayout(downlinkHz, uplinkHz));
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz, forceRebind: true);

        // Fresh creates leave rxant empty (HF-start / rebind without ant=).
        Assert.Equal("", stub.Slices[driver.RxSliceIndex].RxAnt);
        Assert.Equal("", stub.Slices[driver.TxSliceIndex].RxAnt);

        // Detune so EnsureDuplexPassFrequencies must repair and then re-apply band ports.
        stub.SetSliceFrequencyFromOperator(driver.RxSliceIndex, 14_225_000);
        driver.EnsureDuplexPassFrequencies(downlinkHz, uplinkHz, expectedRxMode: "USB", expectedTxMode: "LSB");

        var rx = stub.Slices[driver.RxSliceIndex];
        var tx = stub.Slices[driver.TxSliceIndex];
        Assert.Equal("RX_B", rx.RxAnt);
        Assert.Equal("RX_A", tx.RxAnt);
        Assert.Equal("ANT1", tx.TxAnt);
    }

    [Fact]
    public void SetSatelliteMode_marks_rx_slice_active()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.Equal(driver.RxSliceIndex, stub.ActiveSliceIndex);
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals($"slice set {driver.TxSliceIndex} active=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals($"slice set {driver.RxSliceIndex} active=1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_ignores_mild_on_band_pan_offset()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        const long downlinkHz = 145_950_000;
        const long uplinkHz = 432_146_000;
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(downlinkHz);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(uplinkHz);
        driver.CenterBandPanadapters(downlinkHz, uplinkHz);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId!;
        // On-band and on-screen, but outside the ideal 5 kHz centre. Must not force-rebind.
        stub.SetPanCenterFromOperator(rxPan, downlinkHz - 100_000);
        Assert.True(Math.Abs(stub.PanCentersHz[rxPan] - downlinkHz) > FlexSmartSdrClient.PanCenterToleranceHz);
        Assert.True(
            Math.Abs(stub.PanCentersHz[rxPan] - downlinkHz) < FlexSmartSdrClient.PanCenterDisplayToleranceHz);

        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(downlinkHz, uplinkHz);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice create", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(driver.RxSliceIndex, stub.ActiveSliceIndex);
    }

    [Fact]
    public void CenterBandPanadapters_drags_sticky_hf_pan_via_slice_autopan()
    {
        using var stub = new FlexSmartSdrStubServer(stickyPanCenterUntilAutopan: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        const long downlinkHz = 145_950_000;
        const long uplinkHz = 432_146_000;
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(downlinkHz);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(uplinkHz);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId!;
        stub.SetPanCenterFromOperator(rxPan, 14_100_000);

        stub.ClearCommandBodies();
        driver.CenterBandPanadapters(downlinkHz, uplinkHz);

        Assert.True(
            Math.Abs(stub.PanCentersHz[rxPan] - downlinkHz) <= FlexSmartSdrClient.PanCenterDisplayToleranceHz);
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith($"slice tune {driver.RxSliceIndex} ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("autopan=1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CenterBandPanadapters_drags_sticky_pan_when_slice_already_on_target()
    {
        using var stub = new FlexSmartSdrStubServer(stickyPanCenterUntilAutopan: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        const long downlinkHz = 145_950_000;
        const long uplinkHz = 432_146_000;
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(downlinkHz);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(uplinkHz);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId!;
        stub.SetPanCenterFromOperator(rxPan, 14_100_000);

        stub.ClearCommandBodies();
        driver.CenterBandPanadapters(downlinkHz, uplinkHz);

        Assert.True(
            Math.Abs(stub.PanCentersHz[rxPan] - downlinkHz) <= FlexSmartSdrClient.PanCenterDisplayToleranceHz);
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith($"slice tune {driver.RxSliceIndex} ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("autopan=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith($"slice tune {driver.RxSliceIndex} ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("autopan=1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_accepts_on_band_pan_beyond_display_tolerance()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        const long downlinkHz = 435_850_000;
        const long uplinkHz = 145_952_000;
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(downlinkHz);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(uplinkHz);
        driver.CenterBandPanadapters(downlinkHz, uplinkHz);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId!;
        var txPan = stub.Slices[driver.TxSliceIndex].PanStreamId!;
        stub.SetPanCenterFromOperator(rxPan, downlinkHz - 740_000);
        stub.SetPanCenterFromOperator(txPan, uplinkHz + 740_000);
        Assert.True(Math.Abs(stub.PanCentersHz[rxPan] - downlinkHz) > FlexSmartSdrClient.PanCenterDisplayToleranceHz);
        Assert.True(Math.Abs(stub.PanCentersHz[txPan] - uplinkHz) > FlexSmartSdrClient.PanCenterDisplayToleranceHz);

        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(downlinkHz, uplinkHz);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDuplexPassFrequencies_stops_when_pass_init_cancelled()
    {
        using var stub = new FlexSmartSdrStubServer(stickyPanCenterUntilAutopan: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        const long downlinkHz = 145_950_000;
        const long uplinkHz = 432_146_000;
        driver.BindDuplexSlicesToBandPans(downlinkHz, uplinkHz);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(downlinkHz);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(uplinkHz);

        var rxPan = stub.Slices[driver.RxSliceIndex].PanStreamId!;
        stub.SetPanCenterFromOperator(rxPan, 14_100_000);
        stub.SetSliceFrequencyFromOperator(driver.RxSliceIndex, downlinkHz - 5_000);

        driver.PassInitCancelled = () => true;
        stub.ClearCommandBodies();
        driver.EnsureDuplexPassFrequencies(downlinkHz, uplinkHz);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetSatelliteMode_single_pan_survives_ghost_uhf_pan_stream_id()
    {
        // Mark: status advertised pan 0x40000001 but slice create pan= returned Invalid Stream ID.
        using var stub = new FlexSmartSdrStubServer(
            initialSliceCount: 1,
            emitGhostUhfPanOnSubscribe: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.True(driver.IsSatelliteModeActive);
        Assert.NotEqual(driver.RxSliceIndex, driver.TxSliceIndex);
        Assert.Equal(2, stub.Slices.Count);
        Assert.Equal(driver.RxSliceIndex, stub.ActiveSliceIndex);
        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create", StringComparison.OrdinalIgnoreCase)
                 && !b.Contains("pan=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SupportsVfoExchange_IsFalse()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();
        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port);
        Assert.False(driver.SupportsVfoExchange);
    }
}
