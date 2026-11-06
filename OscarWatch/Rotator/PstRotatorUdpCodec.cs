using System.Globalization;
using System.Text.RegularExpressions;

namespace OscarWatch.Rotator;

/// <summary>
/// PstRotator (YO3DMU) UDP remote-control messages.
/// Commands go to PstRotator's UDP port (default 12000); position replies come back on port + 1
/// as plain text such as <c>AZ:123.4</c> and <c>EL:45.0</c>.
/// </summary>
public static partial class PstRotatorUdpCodec
{
    public const string AzimuthQuery = "<PST>AZ?</PST>";
    public const string ElevationQuery = "<PST>EL?</PST>";
    public const string StopCommand = "<PST><STOP>1</STOP></PST>";

    public static string BuildAzimuthCommand(double azimuthDeg) =>
        $"<PST><AZIMUTH>{Format(azimuthDeg)}</AZIMUTH></PST>";

    public static string BuildElevationCommand(double elevationDeg) =>
        $"<PST><ELEVATION>{Format(elevationDeg)}</ELEVATION></PST>";

    /// <summary>
    /// Reads <c>AZ:</c> and/or <c>EL:</c> values from one reply datagram.
    /// Returns true when at least one axis was found.
    /// </summary>
    public static bool TryParseReply(string? reply, out double? azimuthDeg, out double? elevationDeg)
    {
        azimuthDeg = null;
        elevationDeg = null;
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        azimuthDeg = ReadAxis(AzimuthPattern(), reply);
        elevationDeg = ReadAxis(ElevationPattern(), reply);
        return azimuthDeg is not null || elevationDeg is not null;
    }

    private static string Format(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static double? ReadAxis(Regex pattern, string reply)
    {
        var match = pattern.Match(reply);
        if (!match.Success)
            return null;

        var text = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"AZ\s*[:=]\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AzimuthPattern();

    [GeneratedRegex(@"EL\s*[:=]\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ElevationPattern();
}
