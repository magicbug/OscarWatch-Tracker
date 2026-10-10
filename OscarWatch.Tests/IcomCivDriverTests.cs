using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomCivDriverTests
{
    private static readonly byte[] CivNak = [0xFE, 0xFE, 0x60, 0x00, 0xFA, 0xFD];

    [Fact]
    public void SetFrequencyHz_ack_updates_cached_read_when_live_read_fails()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc910Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.Equal(435_750_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.True(driver.SetFrequencyHz(435_751_000));

        transport.NextReadResponse = [];
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
    public void Ic9700_identity_accepts_model_byte_from_configured_address()
    {
        var transport = new RecordingIcomCivTransport();
        transport.CommandResponses.Enqueue([0xFE, 0xFE, 0x00, 0x60, 0x19, 0x00, 0xA2, 0xFD]);
        var driver = new IcomIc9700Driver(transport);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
        Assert.Equal("1900", transport.SentCommandBodies[^1]);
    }

    [Fact]
    public void Ic9700_identity_rejects_ack_without_a_model_byte()
    {
        var transport = new RecordingIcomCivTransport();
        transport.CommandResponses.Enqueue([0xFE, 0xFE, 0x60, 0x00, 0xFB, 0xFD]);
        var driver = new IcomIc9700Driver(transport);
        driver.Open();

        Assert.False(driver.TryConfirmIdentity());
    }
}
