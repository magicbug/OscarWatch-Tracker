using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

/// <summary>Simulated TS-990S / TS-890S / TS-590 CAT for <see cref="KenwoodHfDriver"/> tests.</summary>
internal sealed class RecordingKenwoodHfTransport(string id) : IKenwoodCatTransport
{
    public string Id { get; set; } = id;
    public long FaHz { get; set; } = 14_074_000;

    /// <summary>Reply to <c>XV;</c> or the TS-590 <c>EX</c> menu read; null means no answer.</summary>
    public string? TransverterStateReply { get; set; } = "XV0;";

    public string? TransverterOffsetReply { get; set; }
    public List<string> SentCommands { get; } = [];
    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;

    public bool SendCommand(string command, int postDelayMs = 50)
    {
        SentCommands.Add(command);
        if (KenwoodCatCodec.TryParseFrequencyHz(command, out var hz) && command.StartsWith("FA", StringComparison.Ordinal))
            FaHz = hz;
        return true;
    }

    public bool SendFireAndForget(string command, int postDelayMs = 50) => SendCommand(command, postDelayMs);

    public string? Transact(string command, int postDelayMs = 50)
    {
        SentCommands.Add(command);
        return command switch
        {
            "ID;" => $"ID{Id};",
            "FA;" => KenwoodHfCatCodec.BuildSetFrequencyCommand(FaHz),
            "XV;" => TransverterStateReply,
            "XO;" => TransverterOffsetReply,
            _ when command.StartsWith("EX", StringComparison.Ordinal) => TransverterStateReply,
            _ => null
        };
    }

    public void Dispose() => IsOpen = false;
}
