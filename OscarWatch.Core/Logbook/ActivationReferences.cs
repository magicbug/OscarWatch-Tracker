using System.Text.RegularExpressions;

namespace OscarWatch.Core.Logbook;

/// <summary>The three activation programmes a logbook can be tagged with.</summary>
public enum ActivationProgramme
{
    Pota,
    Sota,
    Wwff
}

/// <summary>
/// Format checks for POTA, SOTA, and WWFF references, and the ADIF tags they are written as.
/// A value that does not pass is never stored or exported.
/// </summary>
public static partial class ActivationReferences
{
    /// <summary>ADIF tag for the operator's own reference, for example MY_POTA_REF.</summary>
    public static string AdifRefTag(ActivationProgramme programme) => programme switch
    {
        ActivationProgramme.Pota => "MY_POTA_REF",
        ActivationProgramme.Sota => "MY_SOTA_REF",
        ActivationProgramme.Wwff => "MY_WWFF_REF",
        _ => throw new ArgumentOutOfRangeException(nameof(programme))
    };

    /// <summary>Upper case and trimmed, with spaces removed around list commas. Does not validate.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(',', parts).ToUpperInvariant();
    }

    /// <summary>
    /// True when <paramref name="text"/> is empty (no reference) or every reference in it has the
    /// right format for <paramref name="programme"/>. POTA accepts a comma-separated list; SOTA and
    /// WWFF take one reference.
    /// </summary>
    public static bool IsValid(ActivationProgramme programme, string? text)
    {
        var value = Normalize(text);
        if (value.Length == 0)
            return true;

        return programme switch
        {
            ActivationProgramme.Pota => value.Split(',').All(r => PotaRef().IsMatch(r)),
            ActivationProgramme.Sota => SotaRef().IsMatch(value),
            ActivationProgramme.Wwff => WwffRef().IsMatch(value),
            _ => false
        };
    }

    /// <summary>The value to store and export: normalised when valid, otherwise empty.</summary>
    public static string ToStored(ActivationProgramme programme, string? text)
    {
        var value = Normalize(text);
        return IsValid(programme, value) ? value : "";
    }

    // POTA: xxxx-nnnnn with an optional @site suffix, for example US-1356 or K-0001@WA1234.
    [GeneratedRegex(@"^[A-Z0-9]{1,4}-\d{1,5}(@[A-Z0-9]{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PotaRef();

    // SOTA: association/region-number, for example W3/SV-001 or G/LD-003.
    [GeneratedRegex(@"^[A-Z0-9]{1,3}/[A-Z0-9]{1,3}-\d{3,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SotaRef();

    // WWFF: national prefix of 1 to 4 characters, then FF-, then four digits, for example KFF-4655.
    [GeneratedRegex(@"^[A-Z0-9]{1,4}FF-\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex WwffRef();
}
