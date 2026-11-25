using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class Gs232CommandGapTests
{
    [Fact]
    public void First_write_does_not_wait()
    {
        var transport = new RecordingRotatorSerialTransport();
        var sleeps = new List<int>();
        using var rotator = new Gs232Rotator(
            transport,
            commandGapMs: 200,
            nowMs: () => 1_000,
            sleep: sleeps.Add);

        rotator.SetPosition(90, 45, new RotatorSettings { CommandGapMs = 200 });

        Assert.DoesNotContain(200, sleeps);
        Assert.Contains(150, sleeps);
        Assert.Equal("W090 045\r", transport.Written[^1]);
    }

    [Fact]
    public void After_a_reply_the_next_write_waits_the_remaining_gap()
    {
        var clock = 10_000L;
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "AZ=090 EL=045" };
        var sleeps = new List<int>();
        using var rotator = new Gs232Rotator(
            transport,
            commandGapMs: 200,
            nowMs: () => clock,
            sleep: ms =>
            {
                sleeps.Add(ms);
                clock += ms;
            });

        rotator.GetPosition();
        Assert.Equal(["C2\r"], transport.Written);

        sleeps.Clear();
        clock += 20;
        rotator.SetPosition(77, 0, new RotatorSettings { CommandGapMs = 200 });

        Assert.Contains(180, sleeps);
        Assert.Equal("W077 000\r", transport.Written[^1]);
    }

    [Fact]
    public void Gap_of_zero_does_not_wait_before_writes()
    {
        var clock = 10_000L;
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "AZ=090 EL=045" };
        var sleeps = new List<int>();
        using var rotator = new Gs232Rotator(
            transport,
            commandGapMs: 0,
            nowMs: () => clock,
            sleep: ms =>
            {
                sleeps.Add(ms);
                clock += ms;
            });

        rotator.GetPosition();
        sleeps.Clear();
        rotator.SetPosition(90, 45, new RotatorSettings { CommandGapMs = 0 });

        Assert.DoesNotContain(sleeps, ms => ms is > 0 and not 150);
    }

    [Fact]
    public void B_is_sent_when_C2_has_no_elevation_and_skipped_after_one_timeout()
    {
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "AZ=123" };
        using var rotator = new Gs232Rotator(transport, commandGapMs: 0, sleep: _ => { });

        Assert.Equal((123, null), rotator.GetPosition());
        Assert.Equal(["C2\r", "B\r"], transport.Written);

        transport.Written.Clear();
        Assert.Equal((123, null), rotator.GetPosition());
        Assert.Equal(["C2\r"], transport.Written);
    }

    [Fact]
    public void Successful_B_keeps_being_queried()
    {
        var transport = new ScriptedGs232Transport(
            ["AZ=100", "+0030", "AZ=101", "+0031"]);
        using var rotator = new Gs232Rotator(transport, commandGapMs: 0, sleep: _ => { });

        Assert.Equal((100, 30), rotator.GetPosition());
        Assert.Equal((101, 31), rotator.GetPosition());
        Assert.Equal(["C2\r", "B\r", "C2\r", "B\r"], transport.Written);
    }

    [Fact]
    public void CommandGapMs_rejects_values_outside_0_to_1000()
    {
        var settings = new RotatorSettings();
        Assert.Equal(200, settings.CommandGapMs);

        settings.CommandGapMs = 0;
        Assert.Equal(0, settings.CommandGapMs);
        settings.CommandGapMs = 1000;
        Assert.Equal(1000, settings.CommandGapMs);

        settings.CommandGapMs = -1;
        Assert.Equal(1000, settings.CommandGapMs);
        settings.CommandGapMs = 1001;
        Assert.Equal(1000, settings.CommandGapMs);
    }

    [Fact]
    public async Task Settings_round_trip_keeps_command_gap()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var service = new OscarWatch.Core.Services.SettingsService(path);
            service.Load();
            Assert.Equal(200, service.Current.Rotator.CommandGapMs);

            service.Current.Rotator.CommandGapMs = 300;
            await service.SaveAsync();

            var reloaded = new OscarWatch.Core.Services.SettingsService(path);
            reloaded.Load();
            Assert.Equal(300, reloaded.Current.Rotator.CommandGapMs);

            File.WriteAllText(path, """{"rotator":{"port":"COM3"}}""");
            var defaults = new OscarWatch.Core.Services.SettingsService(path);
            defaults.Load();
            Assert.Equal(200, defaults.Current.Rotator.CommandGapMs);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private sealed class ScriptedGs232Transport(IReadOnlyList<string> replies) : IRotatorSerialTransport
    {
        private int _index;

        public List<string> Written { get; } = [];
        public bool IsOpen { get; private set; }
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }

        public void Open() => IsOpen = true;

        public void Write(string text) => Written.Add(text);

        public void Write(byte[] buffer, int offset, int count) =>
            Written.Add(System.Text.Encoding.ASCII.GetString(buffer, offset, count));

        public void DiscardInBuffer()
        {
        }

        public void DiscardOutBuffer()
        {
        }

        public string ReadLine()
        {
            if (_index >= replies.Count)
                throw new TimeoutException();
            return replies[_index++];
        }

        public string ReadExisting() => _index < replies.Count ? replies[_index++] : "";

        public int Read(byte[] buffer, int offset, int count) => 0;

        public void Dispose() => IsOpen = false;
    }
}
