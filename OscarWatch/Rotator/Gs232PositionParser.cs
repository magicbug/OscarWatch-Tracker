// OscarWatch.Troubleshoot compiles this file too. Its own namespace keeps the two copies apart in the tests,
// which reference both assemblies.
#if OSCARWATCH_TROUBLESHOOT
namespace OscarWatch.Troubleshoot;
#else
namespace OscarWatch.Rotator;
#endif

/// <summary>
/// Parses Yaesu GS-232 position responses.
/// GS-232B: <c>AZ=aaa EL=eee</c> (C2/C/B). GS-232A: <c>+0aaa+0eee</c> (C2) or <c>+0nnn</c> (C/B).
/// </summary>
#if OSCARWATCH_TROUBLESHOOT
internal
#else
public
#endif
    static class Gs232PositionParser
{
    /// <summary>Extract az/el from a C2 or single-line response (either format).</summary>
    public static void TryParseParts(string? response, out int? azimuth, out int? elevation)
    {
        azimuth = null;
        elevation = null;
        var line = NormalizeLine(response);
        if (line is null)
            return;

        if (TryParseGs232ACombined(line, out var azCombined, out var elCombined))
        {
            azimuth = azCombined;
            elevation = elCombined;
            return;
        }

        foreach (var part in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseAzimuthToken(part, out var azValue))
                azimuth = azValue;
            else if (TryParseElevationToken(part, out var elValue))
                elevation = elValue;
            else if (TryParsePlusZeroToken(part, out var plusZero))
            {
                if (azimuth is null)
                    azimuth = plusZero;
                else if (elevation is null)
                    elevation = plusZero;
            }
        }
    }

    public static bool TryParseCombined(string? response, out int azimuth, out int elevation)
    {
        TryParseParts(response, out var az, out var el);
        if (az is null || el is null)
        {
            azimuth = 0;
            elevation = 0;
            return false;
        }

        azimuth = az.Value;
        elevation = el.Value;
        return true;
    }

    public static bool TryParseAzimuthLine(string? response, out int azimuth)
    {
        azimuth = 0;
        var line = NormalizeLine(response);
        if (line is null)
            return false;

        TryParseParts(line, out var az, out _);
        if (az is not null)
        {
            azimuth = az.Value;
            return true;
        }

        return TryParsePlusZeroToken(line, out azimuth);
    }

    public static bool TryParseElevationLine(string? response, out int elevation)
    {
        elevation = 0;
        var line = NormalizeLine(response);
        if (line is null)
            return false;

        TryParseParts(line, out _, out var el);
        if (el is not null)
        {
            elevation = el.Value;
            return true;
        }

        return TryParsePlusZeroToken(line, out elevation);
    }

    private static string? NormalizeLine(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var line = response.Trim();
        var firstBreak = line.IndexOfAny(['\r', '\n']);
        return firstBreak >= 0 ? line[..firstBreak].Trim() : line;
    }

    /// <summary>GS-232A C2: <c>+0aaa+0eee</c> (concatenated, no spaces).</summary>
    private static bool TryParseGs232ACombined(string line, out int? azimuth, out int? elevation)
    {
        azimuth = null;
        elevation = null;

        if (!line.StartsWith("+0", StringComparison.Ordinal))
            return false;

        var secondMarker = line.IndexOf("+0", 1, StringComparison.Ordinal);
        if (secondMarker <= 0)
            return false;

        var azDigits = line.AsSpan(2, secondMarker - 2);
        var elDigits = line.AsSpan(secondMarker + 2);
        if (!int.TryParse(azDigits, out var az) || !int.TryParse(elDigits, out var el))
            return false;

        azimuth = az;
        elevation = el;
        return true;
    }

    private static bool TryParseAzimuthToken(string token, out int azimuth)
    {
        azimuth = 0;
        if (!token.StartsWith("AZ=", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(token.AsSpan(3), out azimuth);
    }

    private static bool TryParseElevationToken(string token, out int elevation)
    {
        elevation = 0;
        if (!token.StartsWith("EL=", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(token.AsSpan(3), out elevation);
    }

    /// <summary>GS-232A C/B: <c>+0nnn</c> (three-digit angle).</summary>
    private static bool TryParsePlusZeroToken(string token, out int degrees)
    {
        degrees = 0;
        if (!token.StartsWith("+0", StringComparison.Ordinal))
            return false;

        return int.TryParse(token.AsSpan(2), out degrees);
    }
}
