using OscarWatch.Core.Geo;

namespace OscarWatch.Tests;

public sealed class FootprintGeometryTests
{
    [Theory]
    [InlineData(400, 0, 19.0, 20.0)]
    [InlineData(400, 5, 13.5, 15.5)]
    [InlineData(400, 10, 8.0, 11.0)]
    public void HorizonRadiusDeg_shrinks_as_minimum_elevation_increases(
        double altitudeKm,
        double minimumElevationDeg,
        double minExpectedDeg,
        double maxExpectedDeg)
    {
        var radius = FootprintGeometry.HorizonRadiusDeg(altitudeKm, minimumElevationDeg);
        Assert.InRange(radius, minExpectedDeg, maxExpectedDeg);
    }

    [Fact]
    public void HorizonRadiusDeg_at_zero_is_larger_than_at_pass_minimum_elevation()
    {
        const double altitudeKm = 400;
        var atHorizon = FootprintGeometry.HorizonRadiusDeg(altitudeKm, minimumElevationDeg: 0);
        var atFive = FootprintGeometry.HorizonRadiusDeg(altitudeKm, minimumElevationDeg: 5);

        Assert.True(atHorizon > atFive);
        Assert.InRange(atHorizon - atFive, 4.5, 5.5);
    }
}
