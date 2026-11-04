using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class GrblRotatorTests
{
    private static readonly RotatorSettings Settings = new()
    {
        Type = RotatorType.AntRunner,
        AzimuthRange = RotatorAzimuthRange.Deg360,
        ElevationRange = RotatorElevationRange.Deg180
    };

    [Fact]
    public void SetPosition_sends_absolute_jog_in_millimetres()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);

        driver.SetPosition(90, 45, Settings);

        Assert.Contains("$J=G90 G21 F50 X10.000 Y5.000\n", port.Written);
    }

    [Fact]
    public void SetPosition_while_jogging_to_same_target_does_not_resend()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);
        driver.SetPosition(90, 45, Settings);
        port.State = "Jog";
        port.Written.Clear();

        driver.SetPosition(90.5, 45, Settings);

        Assert.DoesNotContain(port.Written, w => w.StartsWith("$J=", StringComparison.Ordinal));
        Assert.DoesNotContain(port.Written, w => w == "\u0085");
    }

    [Fact]
    public void SetPosition_while_jogging_to_new_target_cancels_then_jogs()
    {
        var port = new ScriptedGrblTransport { IdleAfterCancel = true };
        using var driver = Open(port);
        driver.SetPosition(90, 45, Settings);
        port.State = "Jog";
        port.Written.Clear();

        driver.SetPosition(180, 10, Settings);

        var cancel = port.Written.IndexOf("\u0085");
        var jog = port.Written.IndexOf("$J=G90 G21 F50 X20.000 Y1.111\n");
        Assert.True(cancel >= 0, "expected jog cancel");
        Assert.True(jog > cancel, "expected new jog after cancel");
    }

    [Fact]
    public void SetPosition_when_idle_resends_same_target_for_arrival_retry()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);
        driver.SetPosition(90, 45, Settings);
        port.Written.Clear();

        driver.SetPosition(90, 45, Settings);

        Assert.Contains("$J=G90 G21 F50 X10.000 Y5.000\n", port.Written);
    }

    [Fact]
    public void SetPosition_crosses_north_on_the_next_turn()
    {
        var port = new ScriptedGrblTransport { MachineX = 39.444 };
        using var driver = Open(port);
        driver.SetPosition(355, 10, Settings);
        port.Written.Clear();

        driver.SetPosition(5, 10, Settings);

        Assert.Contains("$J=G90 G21 F50 X40.556 Y1.111\n", port.Written);
    }

    [Fact]
    public void Alarm_sends_unlock_once_per_episode()
    {
        var port = new ScriptedGrblTransport { State = "Alarm" };
        using var driver = Open(port);

        driver.SetPosition(90, 45, Settings);
        driver.SetPosition(91, 45, Settings);

        Assert.Single(port.Written, w => w == "$X\n");
    }

    [Fact]
    public void Idle_controller_never_sends_unlock()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);

        driver.SetPosition(90, 45, Settings);

        Assert.DoesNotContain("$X\n", port.Written);
    }

    [Fact]
    public void Jog_rejected_by_idle_controller_falls_back_to_G0()
    {
        var port = new ScriptedGrblTransport { RejectJog = true };
        using var driver = Open(port);

        driver.SetPosition(90, 45, Settings);

        Assert.Contains("G90 G0 X10.000 Y5.000\n", port.Written);
    }

    [Fact]
    public void Jog_rejected_during_alarm_does_not_fall_back()
    {
        var port = new ScriptedGrblTransport { State = "Alarm", RejectJog = true };
        using var driver = Open(port);

        driver.SetPosition(90, 45, Settings);

        Assert.DoesNotContain(port.Written, w => w.StartsWith("G90 G0", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_sends_jog_cancel_byte()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);
        port.Written.Clear();

        driver.Stop();

        Assert.Equal(["\u0085"], port.Written);
    }

    [Fact]
    public void GetPosition_converts_machine_position_to_degrees()
    {
        var port = new ScriptedGrblTransport { MachineX = -0.5556, MachineY = 5.0 };
        using var driver = Open(port);

        var (az, el) = driver.GetPosition();

        Assert.Equal(355, az);
        Assert.Equal(45, el);
    }

    [Fact]
    public void GetPosition_returns_nulls_when_controller_is_silent()
    {
        var port = new ScriptedGrblTransport { Silent = true };
        using var driver = Open(port);

        Assert.Equal((null, null), driver.GetPosition());
    }

    [Fact]
    public void Open_reads_settings_without_writing_any()
    {
        var port = new ScriptedGrblTransport();
        using var driver = Open(port);

        Assert.Equal(["$$\n"], port.Written);
    }

    [Fact]
    public void Factory_creates_grbl_driver()
    {
        var settings = new RotatorSettings { Type = RotatorType.AntRunner, Port = "COM5", BaudRate = 115200 };

        using var driver = RotatorDriverFactory.Create(settings);

        Assert.IsType<GrblRotator>(driver);
    }

    private static GrblRotator Open(ScriptedGrblTransport port)
    {
        var driver = new GrblRotator(port);
        driver.Open();
        return driver;
    }
}

/// <summary>Minimal GRBL 1.1 responder: <c>?</c> status, <c>ok</c>/<c>error</c> line replies, and <c>$$</c>.</summary>
internal sealed class ScriptedGrblTransport : IRotatorSerialTransport
{
    private readonly Queue<string> _replies = new();

    public List<string> Written { get; } = [];
    public string State { get; set; } = "Idle";
    public double MachineX { get; set; }
    public double MachineY { get; set; }
    public bool RejectJog { get; set; }
    public bool IdleAfterCancel { get; set; }
    public bool Silent { get; set; }
    public bool IsOpen { get; private set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public void Open() => IsOpen = true;

    public void Write(string text)
    {
        Written.Add(text);
        if (Silent)
            return;

        if (text == "?")
        {
            _replies.Enqueue(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"<{State}|MPos:{MachineX:F3},{MachineY:F3},0.000|FS:0,0>\r"));
            return;
        }

        if (text == "$$\n")
        {
            _replies.Enqueue("$3=3\r");
            _replies.Enqueue("$100=1776.000\r");
            _replies.Enqueue("$101=1776.000\r");
            _replies.Enqueue("ok\r");
            return;
        }

        if (text == "$X\n")
        {
            _replies.Enqueue("[MSG:Caution: Unlocked]\r");
            _replies.Enqueue("ok\r");
            return;
        }

        var isJog = text.StartsWith("$J=", StringComparison.Ordinal);
        if (isJog && (RejectJog || State == "Alarm"))
            _replies.Enqueue(State == "Alarm" ? "error:9\r" : "error:3\r");
        else
            _replies.Enqueue("ok\r");
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var text = System.Text.Encoding.Latin1.GetString(buffer, offset, count);
        Written.Add(text);
        if (text == "\u0085" && IdleAfterCancel)
            State = "Idle";
    }

    public void DiscardInBuffer() => _replies.Clear();

    public void DiscardOutBuffer()
    {
    }

    public string ReadLine() => _replies.Count > 0 ? _replies.Dequeue() : throw new TimeoutException();

    public string ReadExisting() => "";

    public int Read(byte[] buffer, int offset, int count) => 0;

    public void Dispose() => IsOpen = false;
}
