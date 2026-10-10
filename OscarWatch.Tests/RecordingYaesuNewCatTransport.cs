namespace OscarWatch.Tests;

internal sealed class RecordingYaesuNewCatTransport : OscarWatch.Rig.IYaesuNewCatTransport
{
    public List<string> SentCommands { get; } = [];
    public Queue<string> Responses { get; } = new();
    public long VfoAHz { get; set; } = 435_750_000;
    /// <summary>Watts returned for <c>PC;</c> reads (FT-991 POWER CONTROL).</summary>
    public int RfPowerWatts { get; set; } = 25;
    public bool IsOpen { get; private set; }

    /// <summary>When true, set commands fail (simulates <c>?;</c> rejection).</summary>
    public bool FailSets { get; set; }

    public void Open() => IsOpen = true;

    /// <summary>Fire-and-forget set — mirrors real Yaesu newcat (no echo required).</summary>
    public bool SendCommand(string command, int postDelayMs = 50)
    {
        if (!IsOpen)
            return false;

        var cmd = Normalize(command);
        SentCommands.Add(cmd);
        if (!FailSets
            && cmd.StartsWith("PC", StringComparison.OrdinalIgnoreCase)
            && cmd != "PC;"
            && OscarWatch.Core.Radio.YaesuFt991CatCodec.TryParsePowerWatts(cmd, out var watts))
            RfPowerWatts = watts;
        return !FailSets;
    }

    public string? Transact(string command, int postDelayMs = 50)
    {
        if (!IsOpen)
            return null;

        var cmd = Normalize(command);
        SentCommands.Add(cmd);

        if (Responses.Count > 0)
            return Responses.Dequeue();

        if (cmd is "FA;" or "FB;")
            return cmd[1] == 'B'
                ? $"FB{VfoAHz:D9};"
                : $"FA{VfoAHz:D9};";

        if (cmd is "PC;")
            return $"PC{RfPowerWatts:D3};";

        // Reads without a canned reply return null; sets must not use Transact in production.
        return null;
    }

    public void Dispose() => IsOpen = false;

    private static string Normalize(string command)
    {
        var cmd = command.TrimEnd();
        return cmd.EndsWith(';') ? cmd : cmd + ";";
    }
}
