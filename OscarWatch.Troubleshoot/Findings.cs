using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

internal enum FindingLevel
{
    Problem,
    Check,
    Note
}

internal sealed record Finding(FindingLevel Level, string Text)
{
    public string Display => $"{Tr.Get("Findings.Level." + Level)}: {Text}";
}

internal sealed record FindingsInput(
    SettingsSnapshot Settings,
    IReadOnlyList<RunningTracker> Running,
    int RecentErrorCount,
    Func<string, bool> PortPresent);

/// <summary>
/// Plain-English reading of the status: settings, hardware switches, serial ports and recent errors.
/// Ports are only looked up, never opened, so a radio is never keyed by DTR or RTS.
/// </summary>
internal static class Findings
{
    public static IReadOnlyList<Finding> Build(FindingsInput input)
    {
        var findings = new List<Finding>();

        if (input.Running.Any(process => process.Responding == false))
            findings.Add(new Finding(FindingLevel.Problem, Tr.Get("Findings.NotResponding")));

        if (input.Running.Count > 1)
            findings.Add(new Finding(FindingLevel.Check, Tr.Format("Findings.SeveralRunning", input.Running.Count)));

        switch (input.Settings.State)
        {
            case SettingsFileState.Missing:
                findings.Add(new Finding(FindingLevel.Note, Tr.Get("Findings.SettingsMissing")));
                break;
            case SettingsFileState.Unreadable:
                findings.Add(new Finding(FindingLevel.Problem, Tr.Get("Findings.SettingsUnreadable")));
                break;
            default:
                AddPortFindings(input, findings);
                break;
        }

        if (input.RecentErrorCount > 0)
            findings.Add(new Finding(FindingLevel.Check, Tr.Format("Findings.RecentErrors", input.RecentErrorCount)));

        if (findings.Count == 0)
            findings.Add(new Finding(FindingLevel.Note, Tr.Get("Findings.NoneFound")));

        return findings;
    }

    /// <summary>True when the port is listed by the system, or for a Unix device path, when the file exists.</summary>
    public static bool IsPortPresent(string port, IReadOnlyCollection<string> systemPorts)
    {
        if (systemPorts.Contains(port, StringComparer.OrdinalIgnoreCase))
            return true;
        return port.StartsWith('/') && File.Exists(port);
    }

    private static void AddPortFindings(FindingsInput input, List<Finding> findings)
    {
        var enabled = input.Settings.SerialPortUses.Where(use => use.Enabled).ToList();

        foreach (var use in enabled)
        {
            var device = DeviceName(use.Device);
            if (use.Port.Length == 0)
            {
                findings.Add(new Finding(FindingLevel.Check, Tr.Format("Findings.NoPort", device)));
                continue;
            }

            if (!input.PortPresent(use.Port))
            {
                findings.Add(new Finding(FindingLevel.Problem, Tr.Format("Findings.PortMissing", device, use.Port)));
                continue;
            }

            findings.Add(new Finding(
                FindingLevel.Note,
                Tr.Format(use.IsGs232 ? "Findings.PortPresentGs232" : "Findings.PortPresent", device, use.Port)));
        }

        foreach (var shared in enabled
                     .Where(use => use.Port.Length > 0)
                     .GroupBy(use => use.Port, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var devices = shared.ToList();
            findings.Add(new Finding(
                FindingLevel.Problem,
                Tr.Format("Findings.PortShared", DeviceName(devices[0].Device), DeviceName(devices[1].Device), shared.Key)));
        }
    }

    private static string DeviceName(HardwareDevice device) => Tr.Get("Hardware." + device);
}
