using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodTs2000DriverTests
{
    [Fact]
    public void Open_does_not_send_autoinfo_before_satellite_entry()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();

        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("AI", StringComparison.Ordinal));
    }

    [Fact]
    public void SetSatelliteMode_on_sends_SA_then_verifies()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.Contains("SA1010110;", transport.SentCommands);
        Assert.Contains("SA;", transport.SentCommands);
        Assert.True(transport.SentCommands.IndexOf("SA1010110;") < transport.SentCommands.IndexOf("SA;"));
        var firstSa = transport.SentCommands.IndexOf("SA1010110;");
        Assert.True(firstSa >= 0);
        Assert.Equal("TO0;", transport.SentCommands[firstSa + 1]);
        Assert.Equal("TO0;", transport.SentCommands[firstSa + 2]);
        Assert.Contains("FA;", transport.SentCommands);
        Assert.Contains("TS1;", transport.SentCommands);
        Assert.Contains("AI2;", transport.SentCommands);
        Assert.Contains("MD2;", transport.SentCommands);
        Assert.Contains("MD1;", transport.SentCommands);
        Assert.Equal("DC00;", transport.SentCommands[0]);
        Assert.Equal("FR;", transport.SentCommands[1]);
        Assert.Equal("DC11;", transport.SentCommands[2]);
        Assert.Equal("FR;", transport.SentCommands[3]);

        var md1Index = transport.SentCommands.IndexOf("MD1;");
        var sa1110Index = transport.SentCommands.IndexOf("SA1011110;");
        Assert.True(sa1110Index >= 0 && md1Index > sa1110Index);
    }

    [Fact]
    public void SetSatelliteMode_on_with_trace_disabled_sends_SA_without_TRACE_bits()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0, traceEnabled: false);
        driver.Open();
        driver.SetSatelliteMode(true);

        Assert.Contains("SA1010000;", transport.SentCommands);
        Assert.Contains("SA1011000;", transport.SentCommands);
        Assert.DoesNotContain("SA1010110;", transport.SentCommands);
        Assert.DoesNotContain("SA1011110;", transport.SentCommands);
        Assert.True(driver.IsSatelliteModeActive);
    }

    [Fact]
    public void TryReadRfPowerWatts_reads_pc_command()
    {
        var transport = new RecordingKenwoodCatTransport { RfPowerWatts = 25 };
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        Assert.True(driver.SupportsRfPowerRead);
        Assert.True(driver.TryReadRfPowerWatts(out var watts));
        Assert.Equal(25.0, watts);
        Assert.Contains("PC;", transport.SentCommands);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("PC", StringComparison.Ordinal) && c != "PC;");
    }

    [Fact]
    public void TrySetRfPowerWatts_sends_pc_command()
    {
        var transport = new RecordingKenwoodCatTransport { RfPowerWatts = 50 };
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        Assert.True(driver.SupportsRfPowerWrite);
        Assert.True(driver.TrySetRfPowerWatts(30));
        Assert.Contains("PC030;", transport.SentCommands);
        Assert.True(driver.TryReadRfPowerWatts(out var watts));
        Assert.Equal(30.0, watts);
    }

    [Fact]
    public void TrySetRfPowerWatts_rejects_out_of_range()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        Assert.False(driver.TrySetRfPowerWatts(4));
        Assert.False(driver.TrySetRfPowerWatts(101));
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("PC", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplySatellitePassFrequencies_sends_pass_programming_and_hold_polls()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        driver.ApplySatellitePassFrequencies(145_900_000, 435_700_000, '2', '1');

        Assert.DoesNotContain("PC050;", transport.SentCommands);
        Assert.Contains("SA1011110;", transport.SentCommands);
        Assert.Contains("DC10;", transport.SentCommands);
        Assert.DoesNotContain(transport.SentCommands, c => c == "FA;");
        Assert.DoesNotContain(transport.SentCommands, c => c is "DC00;" or "DC11;");
    }

    [Fact]
    public void ApplySatelliteDopplerStep_sends_frequency_cluster_without_hold_burst()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        Assert.True(driver.ApplySatelliteDopplerStep(145_900_000, 435_700_000));

        Assert.Contains("FA00145900000;", transport.SentCommands);
        Assert.Contains("FB00435700000;", transport.SentCommands);
        Assert.Contains("SM10000;", transport.SentCommands);
        Assert.Contains("SM00021;", transport.SentCommands);
        Assert.DoesNotContain(transport.SentCommands, c => c == "FA;");
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("FR", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.SentCommands, c => c is "DC00;" or "DC11;");
    }

    [Fact]
    public void SetSatelliteMode_on_SA_unconfirmed_still_tracks_satellite_without_FR()
    {
        var transport = new RecordingKenwoodCatTransport
        {
            SatelliteStatusOn = false,
            AutoConfirmSatelliteOnSet = false
        };
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0, satModeRetryCount: 2, satModeRetryDelayMs: 0);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        Assert.False(driver.IsSatelliteModeActive);
        Assert.True(driver.UsesFaFbSatelliteTracking);

        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("USB");
        driver.SetToneOn(true);
        Assert.False(driver.SetFrequencyHz(145_900_000));

        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("FR", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.SentCommands, c => c == "FA00145900000;");
        Assert.Contains("SA1010110;", transport.SentCommands);
        Assert.Contains("SA1011110;", transport.SentCommands);
        Assert.Contains("MD2;", transport.SentCommands);
    }

    [Fact]
    public void ApplySatelliteDopplerStep_returns_false_when_set_send_fails()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.FailSets = true;
        transport.SentCommands.Clear();

        Assert.False(driver.ApplySatelliteDopplerStep(145_900_000, 435_700_000));
    }

    [Fact]
    public void SetSatelliteMode_off_sends_satellite_exit_sequence()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();
        driver.SetSatelliteMode(false);

        Assert.Equal(KenwoodCatCodec.SatelliteModeExitSequence, transport.SentCommands);
        Assert.DoesNotContain(transport.SentCommands, c => c == "SA;");
    }

    [Fact]
    public void SetFrequencyHz_in_satellite_mode_is_noop_use_doppler_step()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        Assert.False(driver.SetFrequencyHz(435_750_000));
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("FA", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportsVfoExchange_is_true()
    {
        var driver = new KenwoodTs2000Driver(new RecordingKenwoodCatTransport());
        Assert.True(driver.SupportsVfoExchange);
    }

    [Fact]
    public void ExchangeVfos_in_satellite_mode_swaps_FA_and_FB()
    {
        var transport = new RecordingKenwoodCatTransport { FaHz = 145_900_000, FbHz = 435_700_000 };
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();
        driver.ExchangeVfos();

        Assert.Contains("FA00435700000;", transport.SentCommands);
        Assert.Contains("FB00145900000;", transport.SentCommands);
        Assert.Equal(435_700_000, transport.FaHz);
        Assert.Equal(145_900_000, transport.FbHz);
    }

    [Fact]
    public void ExchangeVfos_noop_when_not_in_satellite_mode()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.ExchangeVfos();

        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("FA", StringComparison.Ordinal));
    }

    [Fact]
    public void SetToneHz_encode_sends_TN_command()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetToneHz(67.0, squelchTone: false);

        Assert.Contains("TN01;", transport.SentCommands);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("CN", StringComparison.Ordinal));
    }

    [Fact]
    public void SetMode_on_sub_in_satellite_mode_uses_SA_sub_control_only()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();
        driver.SelectVfo(RigVfo.Sub);
        driver.SetMode("FM");

        var mdIndex = transport.SentCommands.IndexOf("MD4;");
        Assert.True(mdIndex >= 0);
        Assert.Equal("SA1011110;", transport.SentCommands[mdIndex - 1]);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("DC", StringComparison.Ordinal));
    }

    [Fact]
    public void SetToneHz_on_sub_in_satellite_mode_uses_SA_sub_control_only()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();
        driver.SelectVfo(RigVfo.Sub);
        driver.SetToneHz(67.0, squelchTone: false);

        var tnIndex = transport.SentCommands.IndexOf("TN01;");
        Assert.True(tnIndex >= 0);
        Assert.Equal("SA1011110;", transport.SentCommands[tnIndex - 1]);
        Assert.DoesNotContain(transport.SentCommands, c => c.StartsWith("DC", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadFrequencyHz_parses_FA_reply()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();

        var hz = driver.ReadFrequencyHz(RigVfo.Main);
        Assert.Equal(435_750_000, hz);
    }

    [Fact]
    public void Dispose_exits_satellite_mode_when_tracking_was_active()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        driver.Dispose();

        Assert.Equal(KenwoodCatCodec.SatelliteModeExitSequence, transport.SentCommands);
        Assert.False(driver.IsSatelliteModeActive);
    }

    [Fact]
    public void SetSatelliteMode_on_clears_memory_mode_and_exit_restores_it()
    {
        var transport = new RecordingKenwoodCatTransport
        {
            MainVfoSelect = KenwoodCatCodec.VfoSelectMemoryCode,
            SubVfoSelect = KenwoodCatCodec.VfoSelectMemoryCode
        };
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0);
        driver.Open();
        driver.SetSatelliteMode(true);

        var firstSa = transport.SentCommands.IndexOf("SA1010110;");
        Assert.True(firstSa > 0);
        Assert.Equal("DC00;", transport.SentCommands[0]);
        Assert.Equal("FR;", transport.SentCommands[1]);
        Assert.Equal("FR0;", transport.SentCommands[2]);
        Assert.Equal("DC11;", transport.SentCommands[3]);
        Assert.Equal("FR;", transport.SentCommands[4]);
        Assert.Equal("FR0;", transport.SentCommands[5]);

        transport.SentCommands.Clear();
        driver.SetSatelliteMode(false);

        Assert.Equal("DC11;", transport.SentCommands[^5]);
        Assert.Equal("FR2;", transport.SentCommands[^4]);
        Assert.Equal("DC00;", transport.SentCommands[^3]);
        Assert.Equal("FR2;", transport.SentCommands[^2]);
        Assert.Equal("DC00;", transport.SentCommands[^1]);
        Assert.Equal(KenwoodCatCodec.VfoSelectMemoryCode, transport.MainVfoSelect);
        Assert.Equal(KenwoodCatCodec.VfoSelectMemoryCode, transport.SubVfoSelect);
    }

    [Fact]
    public void SetSatelliteMode_exit_restores_main_memory_only()
    {
        var transport = new RecordingKenwoodCatTransport
        {
            MainVfoSelect = KenwoodCatCodec.VfoSelectMemoryCode,
            SubVfoSelect = '0'
        };
        var driver = new KenwoodTs2000Driver(transport, catDelayMs: 0, satModeSettlingDelayMs: 0);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        driver.SetSatelliteMode(false);

        Assert.Equal("DC00;", transport.SentCommands[^3]);
        Assert.Equal("FR2;", transport.SentCommands[^2]);
        Assert.Equal("DC00;", transport.SentCommands[^1]);
        Assert.Equal(KenwoodCatCodec.VfoSelectMemoryCode, transport.MainVfoSelect);
        Assert.Equal('0', transport.SubVfoSelect);
    }

    [Fact]
    public void Identity_accepts_ts2000_and_ts2000x_only()
    {
        var transport = new RecordingKenwoodCatTransport();
        var driver = new KenwoodTs2000Driver(transport);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());

        transport.IdentificationReply = "ID020;";
        Assert.True(driver.TryConfirmIdentity());

        transport.IdentificationReply = "ID000;";
        Assert.False(driver.TryConfirmIdentity());
    }
}
