using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Troubleshoot;

namespace OscarWatch.Tests.Troubleshoot;

public sealed class TroubleshootGs232ProbeTests : IDisposable
{
    private static readonly Gs232Target Target = new(true, "COM5", 4800, false, 450, 180);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-gs232-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public TroubleshootGs232ProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // temp folder
        }
    }

    [Theory]
    [InlineData(120, 45, false, "W120 045")]
    [InlineData(5, 0, false, "W005 000")]
    [InlineData(500, 200, false, "W450 180")]
    [InlineData(-3, -1, false, "W000 000")]
    [InlineData(370, 90, true, "M370")]
    public void Move_formats_like_the_tracker_driver(int az, int el, bool azimuthOnly, string expected)
    {
        Assert.Equal(expected, Gs232Commands.Move(az, el, azimuthOnly, 450, 180));
    }

    [Theory]
    [InlineData(120, 450, 125)]
    [InlineData(448, 450, 443)]
    [InlineData(358, 360, 353)]
    [InlineData(0, 360, 5)]
    public void NudgeAzimuth_turns_back_at_the_limit(int current, int max, int expected)
    {
        Assert.Equal(expected, Gs232Commands.NudgeAzimuth(current, max));
    }

    [Fact]
    public void DescribeBytes_shows_line_endings_and_adds_hex_for_unprintable_bytes()
    {
        Assert.Equal("AZ=123 EL=045\\r\\n", Gs232ProbeLog.DescribeBytes("AZ=123 EL=045\r\n"u8.ToArray()));
        Assert.Equal("A.Z\\r  [hex 41 00 5A 0D]", Gs232ProbeLog.DescribeBytes([0x41, 0x00, 0x5A, 0x0D]));
    }

    [Fact]
    public void FormatLine_has_time_elapsed_tag_and_text()
    {
        var line = Gs232ProbeLog.FormatLine(
            new DateTime(2026, 11, 7, 14, 33, 5, 123), TimeSpan.FromMilliseconds(1500), "TX", "C2\\r");

        Assert.Equal("14:33:05.123  +   1.500s  TX     C2\\r", line);
    }

    [Fact]
    public void ReadPosition_gs232b_uses_c2_only_and_logs_bytes_and_parse()
    {
        var port = new FakePort { ["C2"] = "AZ=123 EL=045\r\n" };
        var log = new Gs232ProbeLog();

        var position = new Gs232Session(port, log, _ => { }).ReadPosition(azimuthOnly: false);

        Assert.Equal(new Gs232Position(123, 45), position);
        Assert.Equal(["C2"], port.Sent);
        Assert.Contains("TX     C2\\r", log.Text);
        Assert.Contains("RX     AZ=123 EL=045\\r\\n", log.Text);
        Assert.Contains("C2: azimuth 123, elevation 45", log.Text);
    }

    [Fact]
    public void ReadPosition_falls_back_to_c_and_b_for_missing_axes()
    {
        var port = new FakePort { ["C"] = "+0210\r", ["B"] = "+0030\r" };
        var log = new Gs232ProbeLog();

        var position = new Gs232Session(port, log, _ => { }).ReadPosition(azimuthOnly: false);

        Assert.Equal(new Gs232Position(210, 30), position);
        Assert.Equal(["C2", "C", "B"], port.Sent);
        Assert.Contains("No reply to C2 within 1000 ms", log.Text);
    }

    [Fact]
    public void ReadPosition_azimuth_only_sends_c_only()
    {
        var port = new FakePort { ["C"] = "AZ=088\r" };

        var position = new Gs232Session(port, new Gs232ProbeLog(), _ => { }).ReadPosition(azimuthOnly: true);

        Assert.Equal(new Gs232Position(88, null), position);
        Assert.Equal(["C"], port.Sent);
    }

    [Fact]
    public void ReadPosition_from_silent_controller_still_sends_every_query()
    {
        var port = new FakePort();
        var log = new Gs232ProbeLog();
        var session = new Gs232Session(port, log, _ => { });

        Assert.False(session.ReadPosition(azimuthOnly: false).HasAny);
        Assert.False(session.ReadPosition(azimuthOnly: false).HasAny);

        Assert.Equal(["C2", "C", "B", "C2", "C", "B"], port.Sent);
    }

    [Fact]
    public void Exchange_logs_stale_bytes_the_tracker_would_discard()
    {
        var port = new FakePort { ["S"] = "" };
        port.Queue("?>\r");
        var log = new Gs232ProbeLog();

        new Gs232Session(port, log, _ => { }).Send("S");

        Assert.Contains("STALE  ?>\\r", log.Text);
        Assert.Contains("TX     S\\r", log.Text);
        Assert.Contains("No reply to S", log.Text);
    }

    [Fact]
    public void Run_logs_port_failure_and_returns_error()
    {
        var log = new Gs232ProbeLog();

        var (value, error) = Gs232Session.Run<bool>(
            () => new FakePort { OpenError = new UnauthorizedAccessException("Access to the port 'COM5' is denied.") },
            Target,
            log,
            _ => true);

        Assert.False(value);
        Assert.Equal("Access to the port 'COM5' is denied.", error);
        Assert.Contains("Opening COM5 at 4800 baud", log.Text);
        Assert.Contains("ERROR  UnauthorizedAccessException: Access to the port 'COM5' is denied.", log.Text);
    }

    [Fact]
    public void Run_opens_runs_and_closes()
    {
        var port = new FakePort { ["C2"] = "AZ=001 EL=002\r" };
        var log = new Gs232ProbeLog();

        var (value, error) = Gs232Session.Run(() => port, Target, log, s => s.ReadPosition(false), _ => { });

        Assert.Null(error);
        Assert.Equal(new Gs232Position(1, 2), value);
        Assert.True(port.Disposed);
        Assert.Contains("INFO   Closed COM5", log.Text);
    }

    [Fact]
    public async Task Read_takes_gs232_target_from_tracker_saved_settings()
    {
        var tracker = new SettingsService(SettingsPath);
        tracker.Load();
        tracker.Current.Rotator.Type = RotatorType.YaesuGs232;
        tracker.Current.Rotator.Port = "COM6";
        tracker.Current.Rotator.BaudRate = 9600;
        tracker.Current.Rotator.AzimuthOnly = true;
        tracker.Current.Rotator.AzimuthRange = RotatorAzimuthRange.Deg360;
        tracker.Current.Rotator.ElevationRange = RotatorElevationRange.Deg90;
        await tracker.SaveAsync();

        Assert.Equal(new Gs232Target(true, "COM6", 9600, true, 360, 90), SettingsFile.Read(SettingsPath).Gs232);
    }

    [Theory]
    [InlineData("""{ "rotator": { "type": "spidMd01", "port": "COM3" } }""", false)]
    [InlineData("""{ "rotator": { "type": "yaesuGs232", "transportKind": "tcp", "port": "COM3" } }""", false)]
    [InlineData("""{ "rotator": { "type": "yaesuGs232", "transportKind": "serial", "port": "COM3" } }""", true)]
    [InlineData("""{ "rotator": { "port": "COM3" } }""", true)]
    public void Read_marks_only_serial_gs232_as_probeable(string json, bool expected)
    {
        File.WriteAllText(SettingsPath, json);

        var target = SettingsFile.Read(SettingsPath).Gs232;

        Assert.NotNull(target);
        Assert.Equal(expected, target.IsSerialGs232);
    }

    [Fact]
    public void Read_uses_tracker_defaults_when_rotator_section_is_missing()
    {
        File.WriteAllText(SettingsPath, "{}");

        Assert.Equal(new Gs232Target(true, "", 4800, false, 450, 180), SettingsFile.Read(SettingsPath).Gs232);
    }

    private sealed class FakePort : IGs232Port
    {
        private readonly Dictionary<string, string> _replies = [];
        private readonly Queue<byte> _incoming = new();

        public List<string> Sent { get; } = [];

        public Exception? OpenError { get; init; }

        public bool Disposed { get; private set; }

        public string this[string command]
        {
            set => _replies[command] = value;
        }

        public void Queue(string text)
        {
            foreach (var b in Encoding.ASCII.GetBytes(text))
                _incoming.Enqueue(b);
        }

        public void Open()
        {
            if (OpenError is not null)
                throw OpenError;
        }

        public void Write(byte[] data)
        {
            var command = Encoding.ASCII.GetString(data).TrimEnd('\r');
            Sent.Add(command);
            if (_replies.TryGetValue(command, out var reply))
                Queue(reply);
        }

        public int BytesToRead => _incoming.Count;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && _incoming.Count > 0)
                buffer[offset + read++] = _incoming.Dequeue();
            return read;
        }

        public void Dispose() => Disposed = true;
    }
}
