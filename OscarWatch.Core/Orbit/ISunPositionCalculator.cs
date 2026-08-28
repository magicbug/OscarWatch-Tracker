using OscarWatch.Core.Models;

namespace OscarWatch.Core.Orbit;

/// <summary>
/// Interface for calculating sun position in ECI coordinates.
/// </summary>
public interface ISunPositionCalculator
{
    /// <summary>
    /// Gets the sun's position in ECI coordinates at the specified UTC time.
    /// </summary>
    /// <param name="utc">UTC time for the calculation</param>
    /// <returns>Sun position in ECI coordinate system</returns>
    EciPosition GetPosition(DateTime utc);
}

/// <summary>
/// Default implementation that delegates to the static SunPositionCalculator.
/// </summary>
public sealed class DefaultSunPositionCalculator : ISunPositionCalculator
{
    /// <summary>Singleton instance for dependency injection.</summary>
    public static readonly DefaultSunPositionCalculator Instance = new();

    private DefaultSunPositionCalculator() { }

    /// <inheritdoc />
    public EciPosition GetPosition(DateTime utc) => SunPositionCalculator.GetPosition(utc);
}