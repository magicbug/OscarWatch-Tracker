namespace OscarWatch.Core.Radio;

/// <summary>
/// Kenwood TS-790A/E ASCII CAT (IF-232C). MAIN transmits, SUB receives; destination via <c>DC</c>.
/// Frequencies use the same 11-digit <c>FA</c> form as other Kenwood radios.
/// </summary>
public static class KenwoodTs790CatCodec
{
    /// <summary>Hamlib / Kenwood model id for the TS-790.</summary>
    public const string ModelId = "007";

    public static string BuildIdentityQuery() => "ID;";

    public static string BuildSelectMainCommand() => "DC0;";

    public static string BuildSelectSubCommand() => "DC1;";

    public static string BuildDestinationQuery() => "DC;";

    /// <summary>VFO A (leave memory / call).</summary>
    public static string BuildSelectVfoACommand() => "FN0;";

    public static string BuildSetFrequencyCommand(long hz) =>
        KenwoodCatCodec.BuildSetFrequencyCommand('A', hz);

    public static string BuildReadFrequencyCommand() =>
        KenwoodCatCodec.BuildReadFrequencyCommand('A');

    public static string BuildSetModeCommand(char modeCode) =>
        KenwoodCatCodec.BuildSetModeCommand(modeCode);

    public static string BuildToneFrequencyCommand(int oneBasedIndex) =>
        KenwoodCatCodec.BuildToneFrequencyCommand(oneBasedIndex);

    public static string BuildToneEnableCommand(bool on) =>
        KenwoodCatCodec.BuildToneEnableCommand(on);

    public static string BuildCtcssFrequencyCommand(int oneBasedIndex) =>
        KenwoodCatCodec.BuildCtcssFrequencyCommand(oneBasedIndex);

    public static string BuildCtcssEnableCommand(bool on) =>
        KenwoodCatCodec.BuildCtcssEnableCommand(on);

    public static bool TryParseIdentity(ReadOnlySpan<char> response, out string modelId)
    {
        modelId = "";
        var span = response.Trim();
        if (span.EndsWith(";"))
            span = span[..^1];

        if (span.Length < 2 || (span[0] is not ('I' or 'i')) || (span[1] is not ('D' or 'd')))
            return false;

        modelId = span[2..].ToString().Trim();
        return modelId.Length > 0;
    }

    public static bool IsTs790Identity(ReadOnlySpan<char> response) =>
        TryParseIdentity(response, out var id)
        && string.Equals(id, ModelId, StringComparison.Ordinal);

    public static bool TryParseDestinationIsSub(ReadOnlySpan<char> response, out bool isSub)
    {
        isSub = false;
        for (var i = 0; i < response.Length - 2; i++)
        {
            if (response[i] is not ('D' or 'd') || response[i + 1] is not ('C' or 'c'))
                continue;

            var code = response[i + 2];
            if (code is '0' or '1')
            {
                isSub = code == '1';
                return true;
            }
        }

        return false;
    }

    public static bool IsReadCommand(string command)
    {
        var body = command.Trim().TrimEnd(';');
        if (body.Equals("FA", StringComparison.OrdinalIgnoreCase)
            || body.Equals("DC", StringComparison.OrdinalIgnoreCase)
            || body.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || body.Equals("IF", StringComparison.OrdinalIgnoreCase))
            return true;

        return KenwoodCatCodec.IsReadCommand(command);
    }
}
