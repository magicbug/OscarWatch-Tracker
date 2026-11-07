using System.Globalization;
using OscarWatch.Troubleshoot;

namespace OscarWatch.Tests.Troubleshoot;

public sealed class TroubleshootFindingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-findings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public TroubleshootFindingsTests()
    {
        Directory.CreateDirectory(_dir);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
    }

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

    [Fact]
    public void Serial_ports_skip_network_radios_and_rotators()
    {
        File.WriteAllText(SettingsPath, """
            {
              "rig": { "enabled": true, "dualRadioEnabled": true,
                "downlink": { "type": "sdrRigCtlTcp", "port": "" },
                "uplink": { "type": "kenwoodTs990s", "port": "COM3", "connection": "lan" } },
              "rotator": { "enabled": true, "type": "spidMd01", "port": "COM4" },
              "gps": { "enabled": true, "connectionKind": "gpsd", "port": "COM5" }
            }
            """);

        Assert.Empty(SettingsFile.Read(SettingsPath).SerialPortUses);
    }

    [Fact]
    public void Serial_ports_list_each_local_device()
    {
        File.WriteAllText(SettingsPath, """
            {
              "rig": { "enabled": true, "type": "icomIc9700", "port": "COM3" },
              "rotator": { "enabled": false, "type": "greenHeronRt21", "port": "COM6", "elevationPort": "COM7" },
              "gps": { "enabled": true, "connectionKind": "serial", "port": "" }
            }
            """);

        var ports = SettingsFile.Read(SettingsPath).SerialPortUses;

        Assert.Equal(
            new[]
            {
                new SerialPortUse(HardwareDevice.Radio, "COM3", true),
                new SerialPortUse(HardwareDevice.Rotator, "COM6", false),
                new SerialPortUse(HardwareDevice.RotatorElevation, "COM7", false),
                new SerialPortUse(HardwareDevice.Gps, "", true)
            },
            ports);
    }

    [Fact]
    public void Serial_ports_mark_a_serial_gs232_rotator()
    {
        File.WriteAllText(SettingsPath, """{ "rotator": { "enabled": true, "type": "yaesuGs232", "port": "COM11" } }""");

        var rotator = Assert.Single(SettingsFile.Read(SettingsPath).SerialPortUses);

        Assert.True(rotator.IsGs232);
    }

    [Fact]
    public void Missing_port_is_a_problem_and_present_port_is_only_a_note()
    {
        var findings = Build(
            [
                new SerialPortUse(HardwareDevice.Radio, "COM3", true),
                new SerialPortUse(HardwareDevice.Rotator, "COM11", true, IsGs232: true)
            ],
            present: ["COM11"]);

        Assert.Contains(findings, f => f.Level == FindingLevel.Problem && f.Text.Contains("COM3", StringComparison.Ordinal));
        var rotator = Assert.Single(findings, f => f.Text.Contains("COM11", StringComparison.Ordinal));
        Assert.Equal(FindingLevel.Note, rotator.Level);
        Assert.Contains("Rotator tab", rotator.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_devices_are_not_checked()
    {
        var findings = Build([new SerialPortUse(HardwareDevice.Gps, "COM9", false)], present: []);

        var only = Assert.Single(findings);
        Assert.Equal(FindingLevel.Note, only.Level);
        Assert.DoesNotContain("COM9", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_device_without_a_port_needs_checking()
    {
        var findings = Build([new SerialPortUse(HardwareDevice.Gps, "", true)], present: []);

        Assert.Contains(findings, f => f.Level == FindingLevel.Check && f.Text.Contains("GPS", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_devices_on_one_port_is_a_problem()
    {
        var findings = Build(
            [
                new SerialPortUse(HardwareDevice.Radio, "COM3", true),
                new SerialPortUse(HardwareDevice.Gps, "com3", true)
            ],
            present: ["COM3"]);

        Assert.Contains(findings, f => f.Level == FindingLevel.Problem && f.Text.Contains("GPS", StringComparison.Ordinal) && f.Text.Contains("Radio", StringComparison.Ordinal));
    }

    [Fact]
    public void Not_responding_and_several_copies_come_first()
    {
        var findings = Findings.Build(new FindingsInput(
            Snapshot([]),
            [new RunningTracker(1, null, Responding: false), new RunningTracker(2, null, Responding: true)],
            RecentErrorCount: 3,
            PortPresent: _ => true));

        Assert.Equal(new[] { FindingLevel.Problem, FindingLevel.Check, FindingLevel.Check }, findings.Select(f => f.Level));
        Assert.Contains("freeze report", findings[0].Text, StringComparison.Ordinal);
        Assert.Contains("3", findings[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_settings_is_a_problem()
    {
        var snapshot = Snapshot([]) with { State = SettingsFileState.Unreadable };

        var findings = Findings.Build(new FindingsInput(snapshot, [], 0, _ => true));

        Assert.Equal(FindingLevel.Problem, Assert.Single(findings).Level);
    }

    [Fact]
    public void Port_presence_ignores_case()
    {
        Assert.True(Findings.IsPortPresent("com4", ["COM4"]));
        Assert.False(Findings.IsPortPresent("COM5", ["COM4"]));
    }

    private static IReadOnlyList<Finding> Build(IReadOnlyList<SerialPortUse> ports, string[] present) =>
        Findings.Build(new FindingsInput(Snapshot(ports), [], 0, port => Findings.IsPortPresent(port, present)));

    private static SettingsSnapshot Snapshot(IReadOnlyList<SerialPortUse> ports)
    {
        var off = new DeviceState(false, "");
        return new SettingsSnapshot(SettingsFileState.Readable, null, null, off, off, off, SerialPorts: ports);
    }
}
