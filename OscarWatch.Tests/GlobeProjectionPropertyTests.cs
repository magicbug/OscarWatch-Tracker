using FsCheck.Xunit;
using OscarWatch.Core.Geo;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for <see cref="GlobeProjection"/>: the inverse recovers the
/// input, and the visibility threshold is the same horizon that
/// <see cref="FootprintGeometry.HorizonRadiusDeg"/> describes.
/// </summary>
public class GlobeProjectionPropertyTests
{
    /// <summary>
    /// Projecting a visible point and unprojecting the result returns the original point, right out to
    /// 99.9 percent of the horizon angle. Points within 0.1 degrees of a pole are skipped, because longitude
    /// has no meaning there.
    /// </summary>
    [Property]
    public bool Unproject_of_project_returns_the_original_point(
        double rawCamLat, double rawCamLon, double rawAlt, double rawDistance, double rawBearing)
    {
        if (!AllFinite(rawCamLat, rawCamLon, rawAlt, rawDistance, rawBearing))
            return true;

        var camera = new GlobeCamera(
            Range(rawCamLat, -89.0, 89.0),
            Range(rawCamLon, -180.0, 180.0),
            Range(rawAlt, 200.0, 40_000.0));
        var horizonDeg = FootprintGeometry.HorizonRadiusDeg(camera.AltitudeKm);
        var distanceDeg = Range(rawDistance, 0.0, 0.999 * horizonDeg);
        var bearingDeg = Range(rawBearing, 0.0, 360.0);

        var (lat, lon) = GlobeProjectionTests.Destination(
            camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, distanceDeg, bearingDeg);
        if (Math.Abs(lat) > 89.9)
            return true;

        var projected = GlobeProjection.Project(camera, lat, lon);
        if (!projected.Visible)
            return false;

        if (!GlobeProjection.TryUnproject(camera, projected.X, projected.Y, out var lat2, out var lon2))
            return false;

        return Math.Abs(lat2 - lat) < 1e-5 && Math.Abs(WrapDegrees(lon2 - lon)) < 1e-5;
    }

    /// <summary>
    /// A point 0.01 degrees inside the horizon angle is visible, and one 0.01 degrees outside is
    /// not, for any camera position, any altitude and any bearing.
    /// </summary>
    [Property]
    public bool Visibility_threshold_matches_the_footprint_horizon(
        double rawCamLat, double rawCamLon, double rawAlt, double rawBearing)
    {
        if (!AllFinite(rawCamLat, rawCamLon, rawAlt, rawBearing))
            return true;

        var camera = new GlobeCamera(
            Range(rawCamLat, -89.0, 89.0),
            Range(rawCamLon, -180.0, 180.0),
            Range(rawAlt, 100.0, 40_000.0));
        var horizonDeg = FootprintGeometry.HorizonRadiusDeg(camera.AltitudeKm);
        var bearingDeg = Range(rawBearing, 0.0, 360.0);

        var (insideLat, insideLon) = GlobeProjectionTests.Destination(
            camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, horizonDeg - 0.01, bearingDeg);
        var (outsideLat, outsideLon) = GlobeProjectionTests.Destination(
            camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, horizonDeg + 0.01, bearingDeg);

        return GlobeProjection.Project(camera, insideLat, insideLon).Visible
            && !GlobeProjection.Project(camera, outsideLat, outsideLon).Visible;
    }

    /// <summary>Every visible point projects inside the limb circle.</summary>
    [Property]
    public bool Visible_points_project_inside_the_limb(
        double rawCamLat, double rawCamLon, double rawAlt, double rawLat, double rawLon)
    {
        if (!AllFinite(rawCamLat, rawCamLon, rawAlt, rawLat, rawLon))
            return true;

        var camera = new GlobeCamera(
            Range(rawCamLat, -89.0, 89.0),
            Range(rawCamLon, -180.0, 180.0),
            Range(rawAlt, 200.0, 40_000.0));

        var point = GlobeProjection.Project(
            camera, Range(rawLat, -90.0, 90.0), Range(rawLon, -180.0, 180.0));
        if (!point.Visible)
            return true;

        var radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
        return radius <= GlobeProjection.LimbRadius(camera.AltitudeKm) + 1e-9;
    }

    /// <summary>
    /// The defining property of the projection: a point's distance from the centre of the picture is its ground
    /// distance from the subpoint, in radians, whatever the position, height and direction.
    /// </summary>
    [Property]
    public bool Distance_from_the_centre_is_the_ground_distance(
        double rawCamLat, double rawCamLon, double rawAlt, double rawDistance, double rawBearing)
    {
        if (!AllFinite(rawCamLat, rawCamLon, rawAlt, rawDistance, rawBearing))
            return true;

        var camera = new GlobeCamera(
            Range(rawCamLat, -89.0, 89.0),
            Range(rawCamLon, -180.0, 180.0),
            Range(rawAlt, 200.0, 40_000.0));
        var horizonDeg = FootprintGeometry.HorizonRadiusDeg(camera.AltitudeKm);
        var distanceDeg = Range(rawDistance, 0.0, 0.999 * horizonDeg);
        var (lat, lon) = GlobeProjectionTests.Destination(
            camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, distanceDeg, Range(rawBearing, 0.0, 360.0));

        var point = GlobeProjection.Project(camera, lat, lon);
        if (!point.Visible)
            return false;

        return Math.Abs(Math.Sqrt(point.X * point.X + point.Y * point.Y) - distanceDeg * Math.PI / 180.0) < 1e-9;
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    /// <summary>Maps any finite value into [min, max] by folding its fractional part.</summary>
    private static double Range(double raw, double min, double max)
    {
        var unit = Math.Abs(raw % 1.0);
        return min + unit * (max - min);
    }

    private static double WrapDegrees(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped > 180.0)
            wrapped -= 360.0;
        if (wrapped < -180.0)
            wrapped += 360.0;
        return wrapped;
    }
}
