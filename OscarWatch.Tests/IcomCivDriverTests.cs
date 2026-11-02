using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomCivDriverTests
{
    private static readonly byte[] CivNak = [0xFE, 0xFE, 0x60, 0x00, 0xFA, 0xFD];

    [Fact]
    public void SetFrequencyHz_ack_does_not_report_cached_frequency_when_live_read_fails()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.True(driver.SetFrequencyHz(435_751_000));

        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));

        transport.NextReadResponse = null;
        Assert.Equal(435_751_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void SetFrequencyHz_without_ack_does_not_update_cached_read()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));

        transport.SetFrequencyResponses.Enqueue(CivNak);
        transport.SetFrequencyResponses.Enqueue(CivNak);
        Assert.False(driver.SetFrequencyHz(435_760_000));

        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Equal(2, transport.SetFrequencyCommandCount);
    }

    [Fact]
    public void SetFrequencyHz_empty_response_does_not_update_cached_read()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));

        transport.SetFrequencyResponses.Enqueue([]);
        transport.SetFrequencyResponses.Enqueue([]);
        Assert.False(driver.SetFrequencyHz(435_760_000));

        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Equal(2, transport.SetFrequencyCommandCount);
    }

    [Fact]
    public void SetFrequencyHz_retries_once_then_succeeds()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        transport.SetFrequencyResponses.Enqueue(CivNak);
        Assert.True(driver.SetFrequencyHz(435_760_000));
        Assert.Equal(2, transport.SetFrequencyCommandCount);

        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(435_760_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void SelectVfo_retries_once_after_nak()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();

        transport.CommandResponses.Enqueue(CivNak);
        driver.SelectVfo(RigVfo.Sub, force: true);

        Assert.Equal(2, transport.CommandCount);
    }

    [Fact]
    public void SelectVfo_without_ack_assumes_success_and_skips_redundant_select()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();

        transport.CommandResponses.Enqueue([]);
        driver.SelectVfo(RigVfo.Main, force: true);
        driver.SelectVfo(RigVfo.Main);

        Assert.Equal(2, transport.CommandCount);
    }

    [Fact]
    public void SelectVfo_without_ack_retries_when_changing_vfo()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();

        transport.CommandResponses.Enqueue([]);
        transport.CommandResponses.Enqueue([]);
        transport.CommandResponses.Enqueue([]);
        driver.SelectVfo(RigVfo.Main, force: true);

        Assert.Equal(3, transport.CommandCount);
    }

    [Fact]
    public void Ic9700_SetMode_DATA_USB_sends_base_mode_then_data_on()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc9700Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);
        transport.SentCommandBodies.Clear();
        driver.SetMode("DATA-USB");

        Assert.Equal(["0601", "1a060101"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic9700_SetMode_DATA_FM_sends_base_mode_then_data_on()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc9700Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);
        transport.SentCommandBodies.Clear();
        driver.SetMode("DATA-FM");

        Assert.Equal(["0605", "1a060101"], transport.SentCommandBodies);
    }

    [Theory]
    [InlineData("DATA-USB", new[] { "0601", "1a060101" })]
    [InlineData("DATA-LSB", new[] { "0600", "1a060101" })]
    [InlineData("USB", new[] { "0601", "1a060000" })]
    public void Ic9100_SetMode_sets_data_mode_for_sideband(string mode, string[] expected)
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc9100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);
        transport.SentCommandBodies.Clear();
        driver.SetMode(mode);

        Assert.Equal(expected, transport.SentCommandBodies);
    }

    [Theory]
    [InlineData(RigType.IcomIc705)]
    [InlineData(RigType.IcomIc7300)]
    [InlineData(RigType.IcomIc905)]
    [InlineData(RigType.IcomIc7100)]
    public void Dual_radio_leg_SetMode_DATA_USB_sends_base_mode_then_data_on(RigType rigType)
    {
        var transport = new RecordingIcomCivTransport();
        IcomCivDriverBase driver = rigType switch
        {
            RigType.IcomIc705 => new IcomIc705Driver(transport),
            RigType.IcomIc7300 => new IcomIc7300Driver(transport),
            RigType.IcomIc905 => new IcomIc905Driver(transport),
            _ => new IcomIc7100Driver(transport)
        };
        driver.Open();
        transport.SentCommandBodies.Clear();
        driver.SetMode("DATA-USB");
        driver.SetMode("USB");

        Assert.Equal(["0601", "1a060101", "0601", "1a060000"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic706_SetMode_DATA_USB_sends_voice_usb_only()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, transport);
        driver.Open();
        transport.SentCommandBodies.Clear();
        driver.SetMode("DATA-USB");

        Assert.Equal(["0601"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic9700_TryReadRfPowerLevel_reads_civ_14_0a()
    {
        var transport = new RecordingIcomCivTransport { RfPowerLevel = 120 };
        var driver = new IcomIc9700Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        Assert.True(driver.SupportsRfPowerRead);
        Assert.True(driver.TryReadRfPowerLevel(out var level));
        Assert.Equal(120, level);
        Assert.Contains("140a", transport.SentCommandBodies);
    }

    [Fact]
    public void Ic9700_TrySetRfPowerLevel_writes_civ_14_0a()
    {
        var transport = new RecordingIcomCivTransport { RfPowerLevel = 200 };
        var driver = new IcomIc9700Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        Assert.True(driver.SupportsRfPowerWrite);
        Assert.True(driver.TrySetRfPowerLevel(102));
        Assert.Contains("140a0102", transport.SentCommandBodies);
        Assert.Equal(102, transport.RfPowerLevel);
        Assert.True(driver.TryReadRfPowerLevel(out var level));
        Assert.Equal(102, level);
    }

    [Fact]
    public void Ic910_SetMode_DATA_USB_sends_voice_usb_only()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();
        driver.SetMode("DATA-USB");

        Assert.Equal(["0601"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic910_SetMode_FMN_sends_fm_with_narrow_filter()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();
        driver.SetMode("FMN");

        Assert.Equal(["060502"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic910_SetMode_FM_sends_fm_with_wide_filter()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();
        driver.SetMode("FM");

        Assert.Equal(["060501"], transport.SentCommandBodies);
    }

    [Fact]
    public void Ic910_identity_accepts_a_frequency_read()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 145_990_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
        Assert.Equal("03", transport.SentCommandBodies[^1]);
    }

    [Fact]
    public void Ic910_identity_rejects_ack_without_a_frequency()
    {
        var transport = new RecordingIcomCivTransport();
        transport.CommandResponses.Enqueue([0xFE, 0xFE, 0x60, 0x00, 0xFB, 0xFD]);
        var driver = new IcomIc910Driver(transport);
        driver.Open();

        Assert.False(driver.TryConfirmIdentity());
    }
}
