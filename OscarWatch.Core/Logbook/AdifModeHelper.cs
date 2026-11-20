namespace OscarWatch.Core.Logbook;

/// <summary>Maps OscarWatch operating modes to ADIF 3.1 <c>MODE</c> / <c>SUBMODE</c> pairs.</summary>
public static class AdifModeHelper
{
    public readonly record struct AdifModeExport(string Mode, string? Submode);

    public static AdifModeExport FromOperatingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return new("", null);

        var upper = mode.Trim().ToUpperInvariant();
        return upper switch
        {
            "LSB" => new("SSB", "LSB"),
            "USB" => new("SSB", "USB"),
            "FMN" or "FM-DATA" or "DATA-FM" => new("FM", null),
            "DATA-USB" or "DATA" => new("SSB", "USB"),
            "CW" => new("CW", null),
            "FM" => new("FM", null),
            "FT8" => new("FT8", null),
            "FT4" => new("MFSK", "FT4"),
            "PKT" or "PACKET" => new("PKT", null),
            _ => new(upper, null)
        };
    }

    /// <summary>
    /// Maps an ADIF 3.1 <c>MODE</c> / <c>SUBMODE</c> pair back to an OscarWatch operating mode
    /// (the reverse of <see cref="FromOperatingMode"/>).
    /// </summary>
    public static string ToOperatingMode(string? mode, string? submode)
    {
        var adifMode = mode?.Trim().ToUpperInvariant() ?? "";
        var adifSub = submode?.Trim().ToUpperInvariant() ?? "";
        if (adifMode.Length == 0)
            return "";

        return (adifMode, adifSub) switch
        {
            ("SSB", "LSB") => "LSB",
            ("SSB", "USB") => "USB",
            ("MFSK", "FT4") => "FT4",
            ("FT8", _) => "FT8",
            ("FM", _) => "FM",
            ("CW", _) => "CW",
            ("PKT", _) => "PKT",
            (_, { Length: > 0 }) => adifSub,
            _ => adifMode
        };
    }

    /// <summary>Human-readable mode label for comments (e.g. <c>USB</c>, <c>SSB</c>).</summary>
    public static string DescribeOperatingMode(string? mode)
    {
        var mapped = FromOperatingMode(mode);
        if (string.IsNullOrWhiteSpace(mapped.Mode))
            return "";

        return string.IsNullOrWhiteSpace(mapped.Submode)
            ? mapped.Mode
            : mapped.Submode;
    }

    public static string? BuildRxModeComment(string? uplinkMode, string? downlinkMode)
    {
        if (string.IsNullOrWhiteSpace(downlinkMode)
            || string.Equals(uplinkMode, downlinkMode, StringComparison.OrdinalIgnoreCase))
            return null;

        var label = DescribeOperatingMode(downlinkMode);
        return string.IsNullOrWhiteSpace(label) ? null : $"RX mode {label}";
    }

    public static string MergeComment(string? userComment, string? rxModeComment)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(userComment))
            parts.Add(userComment.Trim());
        if (!string.IsNullOrWhiteSpace(rxModeComment))
            parts.Add(rxModeComment.Trim());

        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }
}
