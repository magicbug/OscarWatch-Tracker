using System.Globalization;
using OscarWatch.Diagnostics;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class RotatorTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-trace-{Guid.NewGuid():N}");
    private DateTime _now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private string RequestPath => Path.Combine(_dir, RotatorTrace.RequestFileName);

    private string LogDirectory => Path.Combine(_dir, "logs");

    public RotatorTraceTests()
    {
        Directory.CreateDirectory(_dir);
        RotatorTrace.ConfigureForTests(RequestPath, LogDirectory, () => _now);
    }

    public void Dispose()
    {
        RotatorTrace.RestoreDefaults();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // temp folder
        }
    }

    [Fact]
    public void Nothing_is_written_without_a_request()
    {
        var transport = new TracingRotatorTransport(new ScriptedTransport(), "COM6 9600");

        transport.Write("C2\r");

        Assert.False(RotatorTrace.IsActive);
        Assert.False(Directory.Exists(LogDirectory));
    }

    [Fact]
    public void Records_commands_replies_stale_bytes_and_timeouts_while_requested()
    {
        Request(_now.AddMinutes(30));
        var inner = new ScriptedTransport { Waiting = "AZ=179\r\n", Line = "+0180+0045" };
        var transport = new TracingRotatorTransport(inner, "COM6 9600");

        transport.DiscardInBuffer();
        transport.Write("W180 045\r");
        transport.Write([0x57, 0x00, 0x20], 0, 3);
        Assert.Equal("+0180+0045", transport.ReadLine());
        inner.TimeOutNextRead = true;
        Assert.Throws<TimeoutException>(() => transport.ReadLine());
        RotatorTrace.Event("Not arrived");

        var text = ReadTrace();
        Assert.Contains("STALE    [COM6 9600] AZ=179\\r\\n", text, StringComparison.Ordinal);
        Assert.Contains("TX       [COM6 9600] W180 045\\r", text, StringComparison.Ordinal);
        Assert.Contains("TX       [COM6 9600] W.   [hex 57 00 20]", text, StringComparison.Ordinal);
        Assert.Contains("RX       [COM6 9600] +0180+0045 (line)", text, StringComparison.Ordinal);
        Assert.Contains("TIMEOUT  [COM6 9600]", text, StringComparison.Ordinal);
        Assert.Contains("EVENT    Not arrived", text, StringComparison.Ordinal);
        Assert.True(inner.Discarded);
    }

    [Fact]
    public void Stops_when_the_request_ends()
    {
        Request(_now.AddMinutes(1));
        RotatorTrace.Event("first");
        Assert.True(RotatorTrace.IsActive);

        _now = _now.AddMinutes(2);
        RotatorTrace.Event("second");

        Assert.False(RotatorTrace.IsActive);
        var text = ReadTrace();
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text, StringComparison.Ordinal);
        Assert.Contains("Recording time ended.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeBytes_matches_the_troubleshooter_probe()
    {
        Assert.Equal("AZ=123\\r\\n", RotatorTrace.DescribeBytes("AZ=123\r\n"u8));
        Assert.Equal(
            OscarWatch.Troubleshoot.Gs232ProbeLog.DescribeBytes([0x41, 0x00, 0x5A, 0x0D]),
            RotatorTrace.DescribeBytes([0x41, 0x00, 0x5A, 0x0D]));
    }

    private void Request(DateTime untilUtc)
    {
        File.WriteAllText(RequestPath, untilUtc.ToString("O", CultureInfo.InvariantCulture));
        RotatorTrace.ConfigureForTests(RequestPath, LogDirectory, () => _now);
    }

    private string ReadTrace()
    {
        var file = Assert.Single(Directory.GetFiles(LogDirectory, "rotator-trace-*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class ScriptedTransport : IRotatorSerialTransport
    {
        public string Waiting { get; set; } = "";
        public string Line { get; set; } = "";
        public bool TimeOutNextRead { get; set; }
        public bool Discarded { get; private set; }
        public bool IsOpen => true;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }

        public void Open()
        {
        }

        public void Write(string text)
        {
        }

        public void Write(byte[] buffer, int offset, int count)
        {
        }

        public void DiscardInBuffer() => Discarded = true;

        public void DiscardOutBuffer()
        {
        }

        public string ReadLine()
        {
            if (TimeOutNextRead)
            {
                TimeOutNextRead = false;
                throw new TimeoutException();
            }

            return Line;
        }

        public string ReadExisting()
        {
            var text = Waiting;
            Waiting = "";
            return text;
        }

        public int Read(byte[] buffer, int offset, int count) => 0;

        public void Dispose()
        {
        }
    }
}
