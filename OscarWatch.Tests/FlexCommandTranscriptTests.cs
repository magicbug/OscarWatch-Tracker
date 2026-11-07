using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

/// <summary>
/// Locks SmartSDR wire strings so we do not silently regress to bare tune / wiki RXA tokens.
/// </summary>
[Collection(NetworkRadioCollection.Name)]
public class FlexCommandTranscriptTests
{
    [Fact]
    public void Doppler_tune_always_includes_autopan_zero()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.SelectVfo(RigVfo.Main);
        Assert.True(driver.SetFrequencyHz(145_960_000));
        driver.SelectVfo(RigVfo.Sub);
        Assert.True(driver.SetFrequencyHz(435_148_000));

        var tunes = stub.CommandBodies
            .Where(b => b.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, tunes.Count);
        Assert.Contains(tunes, b => b.Equals("slice tune 0 145.96 autopan=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tunes, b => b.Equals("slice tune 1 435.148 autopan=0", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stub.CommandBodies, IsBareTuneWithoutAutopan);
    }

    [Fact]
    public void Antenna_ports_use_slice_set_and_RX_A_underscore_tokens()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.ApplyBandAntennaPorts(
            new RigSettings
            {
                FlexVhfRxAnt = "RX_B",
                FlexUhfRxAnt = "RX_A",
                FlexVhfTxAnt = "XVTR",
                FlexUhfTxAnt = "ANT1"
            },
            downlinkHz: 435_300_000,
            uplinkHz: 145_800_000);

        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 0 rxant=RX_A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 rxant=RX_B", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 1 txant=XVTR", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Contains("rxant=RXA", StringComparison.OrdinalIgnoreCase)
                 || b.Contains("rxant=RXB", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.StartsWith("slice s ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains("rxant=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Legacy_saved_RXA_token_is_sent_as_RX_A()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.ApplyBandAntennaPorts(
            new RigSettings { FlexUhfRxAnt = "RXA", FlexVhfTxAnt = "XVTR" },
            downlinkHz: 435_300_000,
            uplinkHz: 145_800_000);

        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("slice set 0 rxant=RX_A", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Contains("rxant=RXA", StringComparison.OrdinalIgnoreCase)
                 && !b.Contains("rxant=RX_A", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pass_init_pan_centre_uses_display_pan_set_center()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.BindDuplexSlicesToBandPans(145_960_000, 435_148_000);
        driver.CenterBandPanadapters(145_960_000, 435_148_000);

        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000001 center=145.96 autocenter=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals("display pan set 0x40000000 center=435.148 autocenter=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetSatelliteMode_sends_slice_set_active_on_rx()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        stub.ClearCommandBodies();
        driver.SetSatelliteMode(true);

        Assert.Contains(
            stub.CommandBodies,
            b => b.Equals($"slice set {driver.RxSliceIndex} active=1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(driver.RxSliceIndex, stub.ActiveSliceIndex);
    }

    [Fact]
    public void Ao07_layout_binds_slices_to_band_pans_before_tune()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000);
        driver.SelectVfo(RigVfo.Main);
        driver.SetFrequencyHz(145_950_000);
        driver.SelectVfo(RigVfo.Sub);
        driver.SetFrequencyHz(432_146_000);

        var bodies = stub.CommandBodies.ToList();
        var firstRemove = bodies.FindIndex(b =>
            b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        var firstCreate = bodies.FindIndex(b =>
            b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
            && b.Contains(" pan=", StringComparison.OrdinalIgnoreCase));
        var firstTune = bodies.FindIndex(b => b.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase));

        Assert.True(firstRemove >= 0);
        Assert.True(firstCreate > firstRemove);
        Assert.True(firstTune > firstCreate);
        Assert.Contains(bodies, b => b.Contains("slice create freq=145.95 pan=0x40000001", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bodies, b => b.Contains("slice create freq=432.146 pan=0x40000000", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("0x40000001", stub.Slices[driver.RxSliceIndex].PanStreamId);
        Assert.Equal("0x40000000", stub.Slices[driver.TxSliceIndex].PanStreamId);
    }

    [Fact]
    public void Force_rebind_skips_recreate_when_pans_already_match()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        // Initial stub already has RX on UHF pan and TX on VHF pan (RS-44 layout).
        driver.BindDuplexSlicesToBandPans(435_863_000, 145_943_000);
        stub.ClearCommandBodies();

        driver.BindDuplexSlicesToBandPans(435_863_000, 145_943_000, forceRebind: true);

        var bodies = stub.CommandBodies.ToList();
        Assert.DoesNotContain(bodies, b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bodies, b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("0x40000000", stub.Slices[driver.RxSliceIndex].PanStreamId);
        Assert.Equal("0x40000001", stub.Slices[driver.TxSliceIndex].PanStreamId);
        // Frequencies are retuned by pass init after bind; bind itself must not tear down a healthy layout.
    }

    [Fact]
    public void Jo97_to_rs44_layout_flip_rebinds_slices()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);

        driver.BindDuplexSlicesToBandPans(145_865_000, 435_110_100); // JO-97 V/U
        stub.ClearCommandBodies();

        driver.BindDuplexSlicesToBandPans(435_640_000, 145_965_000, forceRebind: true); // RS-44 U/V

        var bodies = stub.CommandBodies.ToList();
        Assert.Contains(bodies, b => b.StartsWith("slice remove ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            bodies,
            b => b.Contains("slice create freq=435.64 pan=0x40000000", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            bodies,
            b => b.Contains("slice create freq=145.965 pan=0x40000001", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("0x40000000", stub.Slices[driver.RxSliceIndex].PanStreamId);
        Assert.Equal("0x40000001", stub.Slices[driver.TxSliceIndex].PanStreamId);
    }

    [Fact]
    public void Bind_fails_when_create_status_omits_pan()
    {
        using var stub = new FlexSmartSdrStubServer(omitPanOnCreateStatus: true);
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.BindDuplexSlicesToBandPans(145_950_000, 432_146_000);

        Assert.Contains(
            stub.CommandBodies,
            b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)
                 && b.Contains(" pan=", StringComparison.OrdinalIgnoreCase));
        // Driver retries once; still cannot verify pan attachment.
        Assert.True(
            stub.CommandBodies.Count(b => b.StartsWith("slice create ", StringComparison.OrdinalIgnoreCase)) >= 2);
    }

    [Fact]
    public void Empty_antenna_settings_send_no_rxant_or_txant()
    {
        using var stub = new FlexSmartSdrStubServer();
        stub.WaitUntilReady();

        using var driver = new FlexRadioDriver("127.0.0.1", stub.Port, catDelayMs: 250);
        driver.Open();
        driver.SetSatelliteMode(true);
        stub.ClearCommandBodies();

        driver.ApplyBandAntennaPorts(new RigSettings(), 435_300_000, 145_800_000);

        Assert.DoesNotContain(
            stub.CommandBodies,
            b => b.Contains("rxant=", StringComparison.OrdinalIgnoreCase)
                 || b.Contains("txant=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBareTuneWithoutAutopan(string body) =>
        body.StartsWith("slice tune ", StringComparison.OrdinalIgnoreCase)
        && !body.Contains("autopan=0", StringComparison.OrdinalIgnoreCase);
}
