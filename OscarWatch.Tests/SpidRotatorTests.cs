using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class SpidRotatorTests
{
    private static readonly RotatorSettings ClassicSettings = new()
    {
        Type = RotatorType.Spid,
        AzimuthRange = RotatorAzimuthRange.Deg450,
        ElevationRange = RotatorElevationRange.Deg180
    };

    [Fact]
    public void Md01_SetPosition_consumes_twelve_byte_reply_before_GetPosition()
    {
        var transport = new QueuedRotatorSerialTransport();
        // Open Stop reply (also detects Rot2 variant), SetPosition reply
        transport.EnqueueRead(BuildRot2Status(10, 5, pulses: 2));
        transport.EnqueueRead(BuildRot2Status(45, 30, pulses: 2));

        using var driver = new SpidRotator(transport, expectSetPositionResponse: true);
        driver.Open();

        var settings = new RotatorSettings
        {
            Type = RotatorType.SpidMd01,
            AzimuthRange = RotatorAzimuthRange.Deg450,
            ElevationRange = RotatorElevationRange.Deg180
        };

        driver.SetPosition(45, 30, settings);
        var (az, el) = driver.GetPosition();

        Assert.Equal(45, az);
        Assert.Equal(30, el);
        Assert.Equal(0, transport.UnreadByteCount);
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public void SetPosition_sends_negative_elevation_down_to_the_spid_floor()
    {
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(0, 0, pulses: 2));

        using var driver = new SpidRotator(transport);
        driver.Open();

        driver.SetPosition(15, -10, ClassicSettings);
        Assert.Equal(-10, DecodeRot2SetElevation(transport.WrittenCommands[^1]));

        driver.SetPosition(15, -40, ClassicSettings);
        Assert.Equal(-10, DecodeRot2SetElevation(transport.WrittenCommands[^1]));
    }

    [Fact]
    public void Classic_SetPosition_does_not_wait_for_reply()
    {
        var transport = new QueuedRotatorSerialTransport();
        // Open / variant detect status reply only
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));

        using var driver = new SpidRotator(transport, expectSetPositionResponse: false);
        driver.Open();

        driver.SetPosition(90, 20, ClassicSettings);

        Assert.Equal(0, transport.UnreadByteCount);
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public void GetPosition_skips_leading_garbage_before_start_byte()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));
        transport.EnqueueRead([0x00, 0xFF, 0x13, .. BuildRot2Status(120, 35, pulses: 2)]);

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal((120, 35), driver.GetPosition());
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public void GetPosition_resyncs_after_stale_partial_frame()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));
        var stale = BuildRot2Status(50, 10, pulses: 2);
        transport.EnqueueRead([.. stale[7..], .. BuildRot2Status(200, 45, pulses: 2)]);

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal((200, 45), driver.GetPosition());
    }

    [Fact]
    public void GetPosition_resends_status_after_a_bad_frame()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));
        var corrupt = BuildRot2Status(90, 10, pulses: 2);
        corrupt[^1] = 0x00;
        transport.EnqueueRead(corrupt);
        transport.EnqueueRead(BuildRot2Status(91, 11, pulses: 2));

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal((91, 11), driver.GetPosition());
        Assert.Equal(3, transport.WriteCount);
    }

    [Fact]
    public void GetPosition_retries_after_a_timeout()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));
        transport.EnqueueTimeout();
        transport.EnqueueRead(BuildRot2Status(180, 20, pulses: 2));

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal((180, 20), driver.GetPosition());
        Assert.Equal(3, transport.WriteCount);
    }

    [Fact]
    public void GetPosition_gives_up_after_three_attempts()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal(((int?)null, (int?)null), driver.GetPosition());
        Assert.Equal(1 + SpidRotator.MaxQueryAttempts, transport.WriteCount);
    }

    [Fact]
    public void Rot1_five_byte_reply_is_detected()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead([SpidRotatorCodec.StartByte, 0x03, 0x07, 0x02, SpidRotatorCodec.EndByte]);
        transport.EnqueueRead([SpidRotatorCodec.StartByte, 0x04, 0x05, 0x00, SpidRotatorCodec.EndByte]);

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();
        clock.Advance(SpidRotator.StatusCacheMs);

        Assert.Equal((90, (int?)null), driver.GetPosition());
    }

    [Fact]
    public void Zero_elevation_digits_are_treated_as_no_elevation()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, -360, pulses: 2));

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();

        Assert.Equal((12, (int?)null), driver.GetPosition());
    }

    [Fact]
    public void Negative_azimuth_is_returned_as_the_controller_reports_it()
    {
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(-86, 80, pulses: 2));

        using var driver = new SpidRotator(transport);
        driver.Open();

        Assert.Equal((-86, 80), driver.GetPosition());
    }

    [Fact]
    public void GetPosition_uses_recent_status_without_a_second_query()
    {
        var clock = new FakeClock();
        var transport = new QueuedRotatorSerialTransport();
        transport.EnqueueRead(BuildRot2Status(12, 8, pulses: 2));
        transport.EnqueueRead(BuildRot2Status(30, 9, pulses: 2));

        using var driver = new SpidRotator(transport, clockMs: clock.Now);
        driver.Open();

        Assert.Equal((12, 8), driver.GetPosition());
        Assert.Equal(1, transport.WriteCount);

        clock.Advance(SpidRotator.StatusCacheMs);
        Assert.Equal((30, 9), driver.GetPosition());
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public void CommandPacingMs_covers_wire_time_at_600_baud()
    {
        Assert.Equal(267, SpidRotator.CommandPacingMs(600));
        Assert.Equal(0, SpidRotator.CommandPacingMs(0));
    }

    [Fact]
    public void Factory_creates_spid_for_md01()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.SpidMd01,
            NetworkHost = "192.168.1.50",
            NetworkPort = 23
        };

        using var driver = RotatorDriverFactory.Create(settings);
        Assert.IsType<SpidRotator>(driver);
    }

    private static double DecodeRot2SetElevation(byte[] command)
    {
        Assert.Equal(SpidRotatorCodec.CommandLength, command.Length);
        Assert.Equal(SpidRotatorCodec.CommandSet, command[11]);
        var pulses = command[10];
        Assert.True(pulses > 0);
        var encoded = (command[6] - (byte)'0') * 1000
            + (command[7] - (byte)'0') * 100
            + (command[8] - (byte)'0') * 10
            + (command[9] - (byte)'0');
        return encoded / (double)pulses - 360.0;
    }

    private static byte[] BuildRot2Status(double azDeg, double elDeg, int pulses)
    {
        var az = (int)Math.Round((360.0 + azDeg) * 10);
        var el = (int)Math.Round((360.0 + elDeg) * 10);
        return
        [
            SpidRotatorCodec.StartByte,
            (byte)(az / 1000 % 10),
            (byte)(az / 100 % 10),
            (byte)(az / 10 % 10),
            (byte)(az % 10),
            (byte)pulses,
            (byte)(el / 1000 % 10),
            (byte)(el / 100 % 10),
            (byte)(el / 10 % 10),
            (byte)(el % 10),
            (byte)pulses,
            SpidRotatorCodec.EndByte
        ];
    }

    private sealed class FakeClock
    {
        private long _nowMs = 1_000;

        public long Now() => _nowMs;

        public void Advance(int ms) => _nowMs += ms;
    }
}

internal sealed class QueuedRotatorSerialTransport : IRotatorSerialTransport
{
    private readonly Queue<byte[]?> _replyFrames = new();
    private readonly Queue<byte> _pending = new();
    private bool _timeoutArmed;

    public int WriteCount { get; private set; }
    public List<byte[]> WrittenCommands { get; } = [];
    public int UnreadByteCount => _pending.Count + _replyFrames.Sum(f => f?.Length ?? 0);
    public bool IsOpen { get; private set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public void EnqueueRead(byte[] bytes) => _replyFrames.Enqueue(bytes);

    /// <summary>The next write gets no reply; reading then throws like a serial read timeout.</summary>
    public void EnqueueTimeout() => _replyFrames.Enqueue(null);

    public void Open() => IsOpen = true;

    public void Write(string text) => ArmNextReply();

    public void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        WrittenCommands.Add(copy);
        ArmNextReply();
    }

    public void DiscardInBuffer() => _pending.Clear();

    public void DiscardOutBuffer()
    {
    }

    public string ReadLine() => throw new NotSupportedException();

    public string ReadExisting() => "";

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_pending.Count == 0 && _timeoutArmed)
        {
            _timeoutArmed = false;
            throw new TimeoutException("Simulated serial read timeout.");
        }

        var copied = 0;
        while (copied < count && _pending.Count > 0)
        {
            buffer[offset + copied] = _pending.Dequeue();
            copied++;
        }

        return copied;
    }

    public void Dispose() => IsOpen = false;

    private void ArmNextReply()
    {
        WriteCount++;
        _timeoutArmed = false;
        if (_replyFrames.Count == 0)
            return;

        var frame = _replyFrames.Dequeue();
        if (frame is null)
        {
            _timeoutArmed = true;
            return;
        }

        foreach (var b in frame)
            _pending.Enqueue(b);
    }
}
