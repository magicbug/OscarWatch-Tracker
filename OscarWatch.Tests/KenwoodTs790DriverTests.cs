using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodTs790DriverTests
{
    [Fact]
    public void TryConfirmIdentity_accepts_model_007()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
        Assert.Contains("ID;", transport.SentCommands);
    }

    [Fact]
    public void SelectVfo_maps_main_to_sub_and_sub_to_main()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);
        Assert.Contains("DC1;", transport.SentCommands);
        Assert.Contains("FN0;", transport.SentCommands);

        transport.SentCommands.Clear();
        driver.SelectVfo(RigVfo.Sub, force: true);
        Assert.Contains("DC0;", transport.SentCommands);
        Assert.DoesNotContain("SA;", transport.SentCommands);
    }

    [Fact]
    public void SetFrequencyHz_writes_fa_on_selected_destination()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main, force: true);
        transport.SentCommands.Clear();

        Assert.True(driver.SetFrequencyHz(435_800_000));
        Assert.Contains("FA00435800000;", transport.SentCommands);
        Assert.Equal(435_800_000, transport.SubFaHz);
    }

    [Fact]
    public void ApplySatellitePassFrequencies_writes_uplink_main_then_downlink_sub_on_full_swap()
    {
        // Radio starts with MAIN on 2 m (uplink band for U/V) and SUB on 70 cm (downlink for V/U).
        // Target is U/V: uplink 435.8 on MAIN, downlink 145.9 on SUB (full band swap).
        var transport = new RecordingKenwoodCatTransport
        {
            FaHz = 145_850_000,
            SubFaHz = 435_750_000
        };
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        Assert.True(driver.ApplySatellitePassFrequencies(145_900_000, 435_800_000));

        var uplinkSet = transport.SentCommands.FindIndex(c => c == "FA00435800000;");
        var downlinkSet = transport.SentCommands.FindIndex(c => c == "FA00145900000;");
        Assert.True(uplinkSet >= 0, string.Join(' ', transport.SentCommands));
        Assert.True(downlinkSet >= 0, string.Join(' ', transport.SentCommands));
        Assert.True(uplinkSet < downlinkSet, string.Join(' ', transport.SentCommands));

        var dc0BeforeUplink = -1;
        for (var i = uplinkSet; i >= 0; i--)
        {
            if (transport.SentCommands[i] != "DC0;")
                continue;
            dc0BeforeUplink = i;
            break;
        }

        Assert.True(dc0BeforeUplink >= 0 && dc0BeforeUplink < uplinkSet);

        Assert.Equal(435_800_000, transport.FaHz);
        Assert.Equal(145_900_000, transport.SubFaHz);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("SA", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplySatellitePassFrequencies_rejects_same_band()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();

        Assert.False(driver.ApplySatellitePassFrequencies(145_825_000, 145_825_000));
    }

    [Fact]
    public void SetToneHz_on_sub_vfo_targets_main_transmit()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Sub, force: true);
        Assert.Contains("DC0;", transport.SentCommands);
        transport.SentCommands.Clear();

        driver.SetToneHz(67.0, squelchTone: false);
        driver.SetToneOn(true);

        // Destination already MAIN from SelectVfo(Sub); tone must not flip to SUB.
        Assert.DoesNotContain("DC1;", transport.SentCommands);
        Assert.Contains("TN01;", transport.SentCommands);
        Assert.Contains("TO1;", transport.SentCommands);
    }

    [Fact]
    public void SetSatelliteMode_is_noop_without_sa_and_reports_active()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        driver.SetSatelliteMode(true);
        Assert.True(driver.IsSatelliteModeActive);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("SA", StringComparison.Ordinal));
        Assert.False(driver.SupportsVfoExchange);
    }

    [Fact]
    public void SetSplitOn_true_does_not_send_sp()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs790Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        driver.SetSplitOn(true);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("SP", StringComparison.Ordinal));
    }
}
