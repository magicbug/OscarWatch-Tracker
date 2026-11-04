using System.Globalization;

namespace OscarWatch.Rotator;

/// <summary>
/// AntRunner GRBL geometry and G-code. Both axes use 360° = 40 mm (X azimuth, Y elevation),
/// matching the Hamlib <c>grbltrk</c> backend and the step/mm the AntRunner firmware expects.
/// </summary>
internal static class GrblCommandFormatter
{
    public const double MillimetresPerTurn = 40.0;

    /// <summary>Default jog feed in mm/min (Hamlib programs a 50 mm/min max rate, about 7.5°/s).</summary>
    public const int DefaultFeedMmPerMin = 50;

    /// <summary>GRBL 1.1 real-time jog cancel.</summary>
    public const byte JogCancel = 0x85;

    public static double DegreesToMillimetres(double degrees) => degrees * MillimetresPerTurn / 360.0;

    public static double MillimetresToDegrees(double millimetres) => millimetres * 360.0 / MillimetresPerTurn;

    /// <summary>
    /// Machine X for a compass azimuth, choosing the turn (−1, 0 or +1) closest to <paramref name="currentMachineXMm"/>
    /// so a pass across north does not unwind a full turn. Machine X stays within −40 to +80 mm.
    /// </summary>
    public static double ResolveMachineX(double azimuthDeg, double? currentMachineXMm)
    {
        var baseX = DegreesToMillimetres(Normalize360(azimuthDeg));
        if (currentMachineXMm is not { } current)
            return baseX;

        var best = baseX;
        var bestDelta = Math.Abs(baseX - current);
        foreach (var candidate in (ReadOnlySpan<double>)[baseX - MillimetresPerTurn, baseX + MillimetresPerTurn])
        {
            var delta = Math.Abs(candidate - current);
            if (delta < bestDelta)
            {
                best = candidate;
                bestDelta = delta;
            }
        }

        return best;
    }

    /// <summary>Compass azimuth (0–360°) for a machine X position on any turn.</summary>
    public static double MachineXToAzimuth(double machineXMm) => Normalize360(MillimetresToDegrees(machineXMm));

    /// <summary>Absolute GRBL 1.1 jog (line feed not included).</summary>
    public static string FormatJog(double xMm, double yMm, int feedMmPerMin = DefaultFeedMmPerMin) =>
        string.Create(CultureInfo.InvariantCulture, $"$J=G90 G21 F{feedMmPerMin} X{xMm:F3} Y{yMm:F3}");

    /// <summary>Absolute rapid move for controllers that reject <c>$J=</c> (line feed not included).</summary>
    public static string FormatRapid(double xMm, double yMm) =>
        string.Create(CultureInfo.InvariantCulture, $"G90 G0 X{xMm:F3} Y{yMm:F3}");

    private static double Normalize360(double degrees)
    {
        var value = degrees % 360.0;
        if (value < 0)
            value += 360.0;
        return value >= 360.0 ? 0 : value;
    }
}
