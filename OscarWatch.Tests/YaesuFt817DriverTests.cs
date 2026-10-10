using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class YaesuFt817DriverTests
{
    [Fact]
    public void Open_sends_dial_lock_off()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();

        Assert.Equal(YaesuFt817CatCodec.DialLockOff.ToArray(), transport.SentFrames[0]);
    }

    [Fact]
    public void SetMode_USB_unlocks_dial_SetMode_FM_locks_dial()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        transport.SentFrames.Clear();

        driver.SetMode("USB");
        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.DialLockOff.ToArray()));

        transport.SentFrames.Clear();
        driver.SetMode("FM");
        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.DialLockOn.ToArray()));
    }

    [Fact]
    public void SetSplitOn_sends_split_opcode()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.SetSplitOn(true);

        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.SplitOn.ToArray()));
    }

    [Fact]
    public void SelectVfoB_toggles_before_set_frequency()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.SetSplitOn(true);
        driver.SelectVfo(RigVfo.VfoB);
        driver.SetFrequencyHz(435_825_000);

        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.ToggleVfo.ToArray()));
        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
    }

    [Fact]
    public void Ft818_reports_correct_rig_type()
    {
        var driver = new YaesuFt818Driver(new RecordingYaesuCatTransport());
        Assert.Equal(RigType.YaesuFt818, driver.RigType);
    }

    [Fact]
    public void Ft857_reports_correct_rig_type_and_unlocks_dial_on_open()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt857Driver(transport);
        driver.Open();

        Assert.Equal(RigType.YaesuFt857, driver.RigType);
        Assert.Equal(YaesuFt817CatCodec.DialLockOff.ToArray(), transport.SentFrames[0]);
    }

    [Fact]
    public void SetFrequencyHz_accepts_10m_hf()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_450_000));
        Assert.Contains(transport.SentFrames, f => f.Length == 5 && f[4] == 0x01);
    }

    [Fact]
    public void Ft818_set_frequency_accepts_10m_hf()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt818Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_450_000));
    }

    [Fact]
    public void SupportsVfoExchange_is_false()
    {
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, new RecordingYaesuCatTransport());
        Assert.False(driver.SupportsVfoExchange);
    }

    [Fact]
    public void Dispose_sends_cat_off()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.Dispose();
        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.DialLockOff.ToArray()));
    }

    [Fact]
    public void SetToneHz_on_main_does_not_toggle_vfo()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);
        transport.SentFrames.Clear();

        driver.SetToneHz(67.0, squelchTone: false);

        Assert.DoesNotContain(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.ToggleVfo.ToArray()));
        Assert.Contains(
            transport.SentFrames,
            f => f.SequenceEqual(YaesuFt817CatCodec.BuildCtcssFrequencyCommand(67.0)));
    }

    [Fact]
    public void SetToneHz_on_vfoB_does_not_toggle_when_already_on_b()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        transport.SentFrames.Clear();

        driver.SelectVfo(RigVfo.VfoB);
        Assert.Contains(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.ToggleVfo.ToArray()));

        transport.SentFrames.Clear();
        driver.SetToneHz(67.0, squelchTone: false);

        Assert.DoesNotContain(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.ToggleVfo.ToArray()));
    }

    [Fact]
    public void SetToneOn_respects_selected_vfo_without_toggle_on_main()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);
        transport.SentFrames.Clear();

        driver.SetToneOn(true);

        Assert.DoesNotContain(transport.SentFrames, f => f.SequenceEqual(YaesuFt817CatCodec.ToggleVfo.ToArray()));
        Assert.Contains(
            transport.SentFrames,
            f => f.SequenceEqual(YaesuFt817CatCodec.BuildCtcssOnCommand(encoderOnly: true)));
    }

    [Fact]
    public void Identity_requires_a_decoded_amateur_frequency()
    {
        var transport = new RecordingYaesuCatTransport();
        var driver = new YaesuFt817Driver(RigType.YaesuFt817, transport);
        driver.Open();

        var frame = new byte[5];
        YaesuFt817CatCodec.EncodeFrequency10Hz(145_900_000, frame);
        frame[4] = 0x01;
        transport.NextQueryResponse = frame;
        Assert.True(driver.TryConfirmIdentity());

        transport.NextQueryResponse = new byte[5];
        Assert.False(driver.TryConfirmIdentity());
    }
}
