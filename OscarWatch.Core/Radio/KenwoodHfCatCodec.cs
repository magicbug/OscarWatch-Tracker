using System.Globalization;
using System.Text;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Radio;

/// <summary>Per-model differences for Kenwood HF radios used as one dual-radio leg.</summary>
public sealed record KenwoodHfProfile(
    RigType Type,
    string ExpectedId,
    bool UsesOperatingModeCommand,
    int? TransverterMenuNumber,
    bool LanCapable,
    bool LanUsesUtf16);

/// <summary>
/// Kenwood TS-990S, TS-890S, TS-590S and TS-590SG CAT used by OscarWatch.
/// Frequencies are 11-digit Hz on <c>FA</c>. The transverter offset (<c>XO</c>) is applied in software
/// because <c>FA</c> always reports the radio's own VFO.
/// </summary>
public static class KenwoodHfCatCodec
{
    public const int Ts990sMaxLanCredentialLength = 8;
    public const int Ts890sMaxLanCredentialLength = 32;

    public static readonly KenwoodHfProfile Ts990s = new(RigType.KenwoodTs990s, "022", true, null, true, true);
    public static readonly KenwoodHfProfile Ts890s = new(RigType.KenwoodTs890s, "024", true, null, true, false);
    public static readonly KenwoodHfProfile Ts590s = new(RigType.KenwoodTs590s, "021", false, 50, false, false);
    public static readonly KenwoodHfProfile Ts590sg = new(RigType.KenwoodTs590sg, "023", false, 56, false, false);

    public static KenwoodHfProfile GetProfile(RigType type) => type switch
    {
        RigType.KenwoodTs990s => Ts990s,
        RigType.KenwoodTs890s => Ts890s,
        RigType.KenwoodTs590s => Ts590s,
        RigType.KenwoodTs590sg => Ts590sg,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a Kenwood HF dual-radio type.")
    };

    public static string BuildIdQuery() => "ID;";

    public static string BuildAutoInfoOffCommand() => "AI0;";

    public static string BuildReadFrequencyCommand() => "FA;";

    public static string BuildSetFrequencyCommand(long vfoHz) =>
        KenwoodCatCodec.BuildSetFrequencyCommand('A', vfoHz);

    public static bool TryParseFrequencyHz(ReadOnlySpan<char> response, out long hz) =>
        KenwoodCatCodec.TryParseFrequencyHz(response, out hz);

    /// <summary>Parse <c>IDnnn;</c> into the three-digit model number.</summary>
    public static bool TryParseId(ReadOnlySpan<char> response, out string id)
    {
        id = "";
        var span = response.Trim();
        if (span.Length < 5 || span[0] is not ('I' or 'i') || span[1] is not ('D' or 'd'))
            return false;

        var digits = span.Slice(2, 3);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
                return false;
        }

        id = digits.ToString();
        return true;
    }

    /// <summary>Commands that put the radio on main / VFO A in simplex before tuning.</summary>
    public static IReadOnlyList<string> BuildMainSimplexCommands(KenwoodHfProfile profile) =>
        profile.UsesOperatingModeCommand ? ["CB0;", "TB0;"] : ["FR0;"];

    /// <summary>
    /// <c>OM0x;</c> on the TS-990S / TS-890S, <c>MDx;</c> on the TS-590. Unknown modes fall back to USB.
    /// </summary>
    public static string BuildSetModeCommand(KenwoodHfProfile profile, string mode)
    {
        var upper = (mode ?? "").Trim().ToUpperInvariant();
        if (profile.UsesOperatingModeCommand)
        {
            var code = upper switch
            {
                "LSB" => '1',
                "CW" => '3',
                "FM" or "FMN" => '4',
                "AM" => '5',
                "DATA-LSB" => 'C',
                "DATA-USB" => 'D',
                "DATA-FM" or "FM-DATA" => 'E',
                _ => '2'
            };
            return $"OM0{code};";
        }

        return KenwoodCatCodec.TryGetModeCode(upper, out var mdCode)
            ? KenwoodCatCodec.BuildSetModeCommand(mdCode)
            : KenwoodCatCodec.BuildSetModeCommand('2');
    }

    /// <summary><c>XV;</c> on the TS-990S / TS-890S, or the transverter menu read on the TS-590.</summary>
    public static string BuildTransverterStateQuery(KenwoodHfProfile profile) =>
        profile.TransverterMenuNumber is { } menu
            ? string.Create(CultureInfo.InvariantCulture, $"EX{menu:D3}0000;")
            : "XV;";

    /// <summary>
    /// <c>XV1;</c> means on. For the TS-590 menu answer (<c>EXmmm0000v;</c>) values 1 and 2 are on
    /// (they only differ in the power-down behaviour).
    /// </summary>
    public static bool TryParseTransverterOn(KenwoodHfProfile profile, ReadOnlySpan<char> response, out bool on)
    {
        on = false;
        var span = response.Trim().TrimEnd(';');
        if (profile.TransverterMenuNumber is { } menu)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"EX{menu:D3}0000");
            if (!span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || span.Length <= prefix.Length)
                return false;

            var value = span[prefix.Length..].Trim();
            if (value.Length != 1 || value[0] is < '0' or > '2')
                return false;

            on = value[0] != '0';
            return true;
        }

        if (span.Length != 3 || span[0] is not ('X' or 'x') || span[1] is not ('V' or 'v'))
            return false;

        if (span[2] is not ('0' or '1'))
            return false;

        on = span[2] == '1';
        return true;
    }

    public static string BuildTransverterOffsetQuery() => "XO;";

    /// <summary>Parse <c>XO</c> + direction (0 plus, 1 minus) + 11-digit offset in Hz.</summary>
    public static bool TryParseTransverterOffset(ReadOnlySpan<char> response, out bool minus, out long offsetHz)
    {
        minus = false;
        offsetHz = 0;
        var span = response.Trim().TrimEnd(';');
        if (span.Length != 2 + 1 + KenwoodCatCodec.FrequencyDigits
            || span[0] is not ('X' or 'x')
            || span[1] is not ('O' or 'o')
            || span[2] is not ('0' or '1'))
        {
            return false;
        }

        long value = 0;
        foreach (var c in span[3..])
        {
            if (c is < '0' or > '9')
                return false;
            value = value * 10 + (c - '0');
        }

        minus = span[2] == '1';
        offsetHz = value;
        return true;
    }

    /// <summary>
    /// Convert a sky (dial) frequency to the radio's own <c>FA</c> frequency.
    /// Plus offset: dial = FA + offset. Minus offset: dial = offset − FA.
    /// </summary>
    public static bool TrySkyToVfoHz(long skyHz, bool transverterOn, bool minus, long offsetHz, out long vfoHz)
    {
        vfoHz = 0;
        if (skyHz <= 0)
            return false;

        if (!transverterOn || offsetHz <= 0)
        {
            vfoHz = skyHz;
            return true;
        }

        vfoHz = minus ? offsetHz - skyHz : skyHz - offsetHz;
        return vfoHz > 0;
    }

    /// <summary>Convert an <c>FA</c> reading to the sky (dial) frequency.</summary>
    public static long VfoToSkyHz(long vfoHz, bool transverterOn, bool minus, long offsetHz)
    {
        if (!transverterOn || offsetHz <= 0)
            return vfoHz;

        return minus ? offsetHz - vfoHz : vfoHz + offsetHz;
    }

    public static string BuildLanConnectCommand() => "##CN;";

    /// <summary><c>##CN1;</c> means the radio accepted the TCP session.</summary>
    public static bool TryParseLanConnect(ReadOnlySpan<char> response, out bool accepted) =>
        TryParseLanFlag(response, "##CN", out accepted);

    /// <summary><c>##ID1;</c> means the account and password were accepted.</summary>
    public static bool TryParseLanLogin(ReadOnlySpan<char> response, out bool accepted) =>
        TryParseLanFlag(response, "##ID", out accepted);

    /// <summary>
    /// TS-890S: <c>##ID</c> + account type (0 administrator, 1 user) + 2-digit lengths + account + password.
    /// TS-990S: <c>##ID</c> + 1-digit lengths (max 8) + account + password.
    /// </summary>
    public static bool TryBuildLanLoginCommand(
        KenwoodHfProfile profile,
        string account,
        string password,
        bool administratorAccount,
        out string command)
    {
        command = "";
        if (!profile.LanCapable || string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
            return false;

        var max = profile.LanUsesUtf16 ? Ts990sMaxLanCredentialLength : Ts890sMaxLanCredentialLength;
        if (account.Length > max || password.Length > max || account.Contains(';') || password.Contains(';'))
            return false;

        command = profile.LanUsesUtf16
            ? string.Create(CultureInfo.InvariantCulture, $"##ID{account.Length}{password.Length}{account}{password};")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"##ID{(administratorAccount ? '0' : '1')}{account.Length:D2}{password.Length:D2}{account}{password};");
        return true;
    }

    /// <summary>TS-890S LAN is UTF-8; TS-990S LAN is UTF-16 (little-endian, no byte-order mark).</summary>
    public static Encoding GetLanEncoding(KenwoodHfProfile profile) =>
        profile.LanUsesUtf16
            ? new UnicodeEncoding(bigEndian: false, byteOrderMark: false)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static bool TryParseLanFlag(ReadOnlySpan<char> response, string prefix, out bool accepted)
    {
        accepted = false;
        var span = response.Trim().TrimEnd(';');
        if (!span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || span.Length != prefix.Length + 1)
            return false;

        var flag = span[prefix.Length];
        if (flag is not ('0' or '1'))
            return false;

        accepted = flag == '1';
        return true;
    }
}
