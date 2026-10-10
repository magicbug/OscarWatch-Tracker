using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

internal sealed class RecordingKenwoodCatTransport : IKenwoodCatTransport
{
    /// <summary>Watts returned for <c>PC;</c> reads (TS-2000 transmit power, 005–100).</summary>
    public int RfPowerWatts { get; set; } = 50;

    public long FaHz { get; set; } = 435_750_000;
    public long FbHz { get; set; } = 145_900_000;
    public bool SatelliteStatusOn { get; set; } = true;

    /// <summary>
    /// When false, <c>SA1010110;</c> does not force <see cref="SatelliteStatusOn"/> so unconfirmed SATL can be tested.
    /// </summary>
    public bool AutoConfirmSatelliteOnSet { get; set; } = true;

    /// <summary>When true, set commands fail (simulates write / ?; rejection).</summary>
    public bool FailSets { get; set; }

    /// <summary>Command prefixes (e.g. <c>SM</c>, <c>TO0</c>) that return false from set sends.</summary>
    public HashSet<string> RejectedPrefixes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional per-command rejection hook for tests (return true to reject).</summary>
    public Func<string, bool>? ShouldRejectSet { get; set; }

    /// <summary>Reply for <c>ID;</c>. 019 is a TS-2000; 020 is a TS-2000X.</summary>
    public string IdentificationReply { get; set; } = "ID019;";

    public char MainVfoSelect { get; set; } = '0';
    public char SubVfoSelect { get; set; } = '0';
    public List<string> SentCommands { get; } = [];
    public bool IsOpen { get; private set; }

    private bool _ctrlOnSubReceiver;

    public void Open() => IsOpen = true;

    public bool SendFireAndForget(string command, int postDelayMs = 50) =>
        SendCommand(command, postDelayMs);

    public bool SendCommand(string command, int postDelayMs = 50)
    {
        var normalized = Normalize(command);
        SentCommands.Add(normalized);
        if (FailSets || IsRejected(normalized))
            return false;

        ApplySatelliteModeCommand(normalized);
        ApplySetFrequency(normalized);
        ApplyDcCommand(normalized);
        ApplyVfoSelectCommand(normalized);
        return true;
    }

    private bool IsRejected(string normalized) =>
        ShouldRejectSet?.Invoke(normalized) == true
        || FailSets
        || (RejectedPrefixes.Count > 0
            && RejectedPrefixes.Any(prefix =>
                normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    public string? Transact(string command, int postDelayMs = 50)
    {
        var normalized = Normalize(command);
        SentCommands.Add(normalized);
        ApplySatelliteModeCommand(normalized);
        ApplyDcCommand(normalized);
        ApplyVfoSelectCommand(normalized);
        return normalized switch
        {
            "SA1010110;" => SatelliteStatusOn ? "SA1;" : "SA0;",
            "SA1010000;" => SatelliteStatusOn ? "SA1;" : "SA0;",
            "SA1011110;" => SatelliteStatusOn ? "SA1;" : "SA0;",
            "SA1011000;" => SatelliteStatusOn ? "SA1;" : "SA0;",
            "SA0010000;" => "SA0;",
            "SA0;" => "SA0;",
            "RX;" => "RX0;",
            "SA;" => SatelliteStatusOn ? "SA1;" : "SA0;",
            "FR;" => $"FR{(_ctrlOnSubReceiver ? SubVfoSelect : MainVfoSelect)};",
            "FA;" => KenwoodCatCodec.BuildSetFrequencyCommand('A', FaHz),
            "FB;" => KenwoodCatCodec.BuildSetFrequencyCommand('B', FbHz),
            "PC;" => $"PC{RfPowerWatts:D3};",
            "ID;" => IdentificationReply,
            _ when TryRememberPowerSet(normalized) => normalized,
            _ => KenwoodCatCodec.IsReadCommand(normalized) ? null : normalized
        };
    }

    public void Dispose() => IsOpen = false;

    private bool TryRememberPowerSet(string normalized)
    {
        if (!normalized.StartsWith("PC", StringComparison.OrdinalIgnoreCase) || normalized == "PC;")
            return false;
        if (KenwoodCatCodec.TryParsePowerWatts(normalized, out var watts))
            RfPowerWatts = watts;
        return true;
    }

    private void ApplySatelliteModeCommand(string normalized)
    {
        if (normalized is "SA0010000;" or "SA0;")
            SatelliteStatusOn = false;
        else if (AutoConfirmSatelliteOnSet
            && normalized is "SA1010110;" or "SA1010000;" or "SA1011110;" or "SA1011000;")
            SatelliteStatusOn = true;
    }

    private void ApplySetFrequency(string normalized)
    {
        if (KenwoodCatCodec.TryParseFrequencyHz(normalized, out var hz) && hz > 0)
        {
            if (normalized.StartsWith("FA", StringComparison.OrdinalIgnoreCase))
                FaHz = hz;
            else if (normalized.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
                FbHz = hz;
        }
    }

    private void ApplyDcCommand(string normalized)
    {
        if (!normalized.StartsWith("DC", StringComparison.OrdinalIgnoreCase) || normalized.Length < 5)
            return;

        _ctrlOnSubReceiver = normalized[3] == '1';
    }

    private void ApplyVfoSelectCommand(string normalized)
    {
        if (!normalized.StartsWith("FR", StringComparison.OrdinalIgnoreCase) || normalized.Length < 4)
            return;

        var select = normalized[2];
        if (select is < '0' or > '9')
            return;

        if (_ctrlOnSubReceiver)
            SubVfoSelect = select;
        else
            MainVfoSelect = select;
    }

    private static string Normalize(string command)
    {
        var cmd = command.Trim();
        return cmd.EndsWith(';') ? cmd : cmd + ";";
    }
}
