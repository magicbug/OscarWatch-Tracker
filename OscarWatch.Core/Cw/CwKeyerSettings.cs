using OscarWatch.Core.Hardware;

namespace OscarWatch.Core.Cw;

/// <summary>CW keyer on the logbook: macro buttons F1 to F12 and how the uplink radio is keyed.</summary>
public sealed class CwKeyerSettings
{
    public const int MessageCount = 12;
    public const int MinWpm = 5;
    public const int MaxWpm = 60;
    public const int DefaultWpm = 20;
    public const int MaxLabelLength = 12;
    public const int MaxMessageLength = 200;
    public const int MinAlignDurationSeconds = 3;
    public const int MaxAlignDurationSeconds = 8;
    public const int DefaultAlignDurationSeconds = 5;
    public const int MinAlignTargetPitchHz = 300;
    public const int MaxAlignTargetPitchHz = 1200;
    public const int DefaultAlignTargetPitchHz = 700;
    public const int MinAlignMaxCorrectionHz = 200;
    public const int MaxAlignMaxCorrectionHz = 2500;
    public const int DefaultAlignMaxCorrectionHz = 1500;

    public bool Enabled { get; set; }

    public int Wpm { get; set; } = DefaultWpm;

    /// <summary>Hard transmit duration cap for an automatic CW uplink alignment probe, in seconds.</summary>
    public int AlignMaxDurationSeconds { get; set; } = DefaultAlignDurationSeconds;

    /// <summary>Target audio pitch for automatic CW uplink alignment, in hertz.</summary>
    public int AlignTargetPitchHz { get; set; } = DefaultAlignTargetPitchHz;

    /// <summary>Maximum per-probe uplink correction allowed before refusal, in hertz.</summary>
    public int AlignMaxCorrectionHz { get; set; } = DefaultAlignMaxCorrectionHz;

    /// <summary>Line that keys the radio when a COM key line is used.</summary>
    public PttLine KeyLine { get; set; } = PttLine.Dtr;

    /// <summary>When true the key line is on <see cref="SeparatePort"/>, otherwise on the open CAT port.</summary>
    public bool UseSeparatePort { get; set; }

    public string SeparatePort { get; set; } = "";

    /// <summary>
    /// Key a radio that forms Morse from text (Icom CI-V, Kenwood KY) with a COM key line instead.
    /// </summary>
    public bool UseComLineOnTextRadios { get; set; }

    public List<CwKeyerMessage> Messages { get; set; } = CreateDefaultMessages();

    public static int ClampWpm(int wpm) => Math.Clamp(wpm, MinWpm, MaxWpm);

    public static int ClampAlignDurationSeconds(int seconds) =>
        Math.Clamp(seconds, MinAlignDurationSeconds, MaxAlignDurationSeconds);

    public static int ClampAlignTargetPitchHz(int hz) =>
        Math.Clamp(hz, MinAlignTargetPitchHz, MaxAlignTargetPitchHz);

    public static int ClampAlignMaxCorrectionHz(int hz) =>
        Math.Clamp(hz, MinAlignMaxCorrectionHz, MaxAlignMaxCorrectionHz);

    public static List<CwKeyerMessage> CreateDefaultMessages()
    {
        var messages = new List<CwKeyerMessage>
        {
            new() { Label = "CQ", Text = "CQ SAT DE {MYCALL} {MYCALL}" },
            new() { Label = "Call", Text = "{CALL}" },
            new() { Label = "Report", Text = "{CALL} {SENTRST} {MYGRID}" },
            new() { Label = "My call", Text = "{MYCALL}" },
            new() { Label = "Grid", Text = "{MYGRID}" },
            new() { Label = "TU", Text = "TU {MYCALL} SK" }
        };
        while (messages.Count < MessageCount)
            messages.Add(new CwKeyerMessage());
        return messages;
    }

    /// <summary>Exactly twelve slots, speed in range, no null strings.</summary>
    public void Normalise()
    {
        Wpm = ClampWpm(Wpm);
        AlignMaxDurationSeconds = ClampAlignDurationSeconds(AlignMaxDurationSeconds);
        AlignTargetPitchHz = ClampAlignTargetPitchHz(AlignTargetPitchHz);
        AlignMaxCorrectionHz = ClampAlignMaxCorrectionHz(AlignMaxCorrectionHz);
        SeparatePort = SeparatePort?.Trim() ?? "";
        if (!Enum.IsDefined(KeyLine))
            KeyLine = PttLine.Dtr;

        Messages ??= CreateDefaultMessages();
        Messages.RemoveAll(m => m is null);
        if (Messages.Count > MessageCount)
            Messages.RemoveRange(MessageCount, Messages.Count - MessageCount);
        while (Messages.Count < MessageCount)
            Messages.Add(new CwKeyerMessage());

        foreach (var message in Messages)
        {
            message.Label = Truncate(message.Label?.Trim() ?? "", MaxLabelLength).Trim();
            message.Text = Truncate(message.Text?.Trim() ?? "", MaxMessageLength).Trim();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public sealed class CwKeyerMessage
{
    /// <summary>Short name on the button, after the key number.</summary>
    public string Label { get; set; } = "";

    /// <summary>Message with macros such as <c>{MYCALL}</c>. Empty means the key does nothing.</summary>
    public string Text { get; set; } = "";
}
