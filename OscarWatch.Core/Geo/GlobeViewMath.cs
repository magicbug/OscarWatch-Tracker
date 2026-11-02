using OscarWatch.Core.Models;

namespace OscarWatch.Core.Geo;

/// <summary>Geometry helpers for the Earth View that are independent of any drawing code.</summary>
public static class GlobeViewMath
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Angular radius, in degrees, of the area from which a satellite at <paramref name="altitudeKm"/>
    /// is above <paramref name="minimumElevationDeg"/>. Zero when the altitude is not positive or the
    /// elevation is 90 degrees or more.
    /// </summary>
    /// <remarks>
    /// The exact central angle at elevation e is acos(R / (R + h) * cos e) - e. At 0 degrees this is the
    /// horizon, <see cref="FootprintGeometry.HorizonRadiusDeg"/>, but subtracting the elevation from the
    /// horizon angle is not the same thing for any other elevation: for a 400 km satellite and a 20 degree
    /// minimum it gives 0 where the true radius is about 7.9 degrees. The main map only draws the
    /// 0 degree footprint, so only the Earth View needs this form.
    /// </remarks>
    public static double FootprintRadiusDeg(double altitudeKm, double minimumElevationDeg)
    {
        if (!double.IsFinite(altitudeKm) || !double.IsFinite(minimumElevationDeg) || altitudeKm <= 0)
            return 0;

        var elevationRad = Math.Max(minimumElevationDeg, 0.0) * Math.PI / 180.0;
        if (elevationRad >= Math.PI / 2)
            return 0;

        var ratio = EarthRadiusKm / (EarthRadiusKm + altitudeKm);
        var centralAngleRad = Math.Acos(Math.Clamp(ratio * Math.Cos(elevationRad), -1, 1)) - elevationRad;
        return centralAngleRad > 0 ? centralAngleRad * 180.0 / Math.PI : 0;
    }

    /// <summary>
    /// A closed ring of points at a fixed angular distance from the subpoint, in bearing order.
    /// The first point is not repeated at the end. Empty when the radius is not positive.
    /// </summary>
    public static IReadOnlyList<GeoCoordinate> FootprintRing(
        GeoCoordinate subpoint, double radiusDeg, int points = 180)
    {
        if (!double.IsFinite(radiusDeg) || radiusDeg <= 0 || points < 3)
            return [];

        var ring = new List<GeoCoordinate>(points);
        for (var i = 0; i < points; i++)
        {
            var bearingDeg = 360.0 * i / points;
            var (lat, lon) = Destination(subpoint.LatitudeDeg, subpoint.LongitudeDeg, radiusDeg, bearingDeg);
            ring.Add(new GeoCoordinate(lat, lon));
        }

        return ring;
    }

    /// <summary>True when the station is within <paramref name="radiusDeg"/> of the subpoint on the surface.</summary>
    public static bool IsInsideFootprint(
        GeoCoordinate subpoint, double stationLatDeg, double stationLonDeg, double radiusDeg)
    {
        if (!double.IsFinite(radiusDeg) || radiusDeg <= 0
            || !double.IsFinite(stationLatDeg) || !double.IsFinite(stationLonDeg))
            return false;

        return CentralAngleDeg(subpoint.LatitudeDeg, subpoint.LongitudeDeg, stationLatDeg, stationLonDeg) <= radiusDeg;
    }

    /// <summary>Great-circle angle between two points, in degrees.</summary>
    public static double CentralAngleDeg(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        var lat1 = lat1Deg * Math.PI / 180.0;
        var lat2 = lat2Deg * Math.PI / 180.0;
        var dLon = (lon2Deg - lon1Deg) * Math.PI / 180.0;

        // The atan2 form stays accurate for very small and very large angles, unlike acos.
        var sinLat1 = Math.Sin(lat1);
        var cosLat1 = Math.Cos(lat1);
        var sinLat2 = Math.Sin(lat2);
        var cosLat2 = Math.Cos(lat2);
        var cosD = Math.Cos(dLon);
        var sinD = Math.Sin(dLon);

        var y = Math.Sqrt(
            Math.Pow(cosLat2 * sinD, 2) +
            Math.Pow(cosLat1 * sinLat2 - sinLat1 * cosLat2 * cosD, 2));
        var x = sinLat1 * sinLat2 + cosLat1 * cosLat2 * cosD;
        return Math.Atan2(y, x) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Projects a ground track onto the globe and splits it into runs of points that are on the
    /// visible hemisphere. A run ends where the track passes the limb or where the input has a
    /// <see cref="double.NaN"/> latitude (the gap marker used by the ground track code), and a new
    /// run starts when it comes back into view. Runs with fewer than two points are dropped, as they
    /// cannot be drawn as a line. Coordinates are in radians of arc on the projection plane.
    /// </summary>
    /// <remarks>
    /// Where the track crosses the limb between two samples, the run is extended to the crossing itself,
    /// found by bisection along the great-circle arc, so the line reaches the edge of the globe. The focused track is sampled every
    /// 60 seconds, which for a low orbit is several degrees of arc: stopping at the last visible sample
    /// would leave a visible gap. A NaN gap is never bridged.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> ProjectTrack(
        GlobeCamera camera, IReadOnlyList<GeoCoordinate> track)
    {
        var basis = new GlobeBasis(camera);
        if (!basis.IsValid || track.Count < 2)
            return [];

        var runs = new List<IReadOnlyList<(double X, double Y)>>();
        List<(double X, double Y)>? current = null;
        GeoCoordinate? previous = null; // the last sample, or null at the start and after a NaN gap
        var previousVisible = false;

        foreach (var p in track)
        {
            if (double.IsNaN(p.LatitudeDeg))
            {
                CloseRun();
                previous = null;
                continue;
            }

            var projected = basis.Project(p.LatitudeDeg, p.LongitudeDeg);
            if (projected.Visible)
            {
                if (current is null && previous is { } hiddenBefore && !previousVisible)
                    current = [LimbCrossing(basis, visible: p, hidden: hiddenBefore)];

                current ??= [];
                current.Add((projected.X, projected.Y));
            }
            else
            {
                if (current is not null && previous is { } visibleBefore && previousVisible)
                    current.Add(LimbCrossing(basis, visible: visibleBefore, hidden: p));

                CloseRun();
            }

            previous = p;
            previousVisible = projected.Visible;
        }

        CloseRun();
        return runs;

        void CloseRun()
        {
            if (current is { Count: >= 2 })
                runs.Add(current);
            current = null;
        }
    }

    /// <summary>
    /// The projected point where the track between a visible sample and a hidden one meets the limb.
    /// Bisection on visibility along the short great-circle arc between the two samples. Interpolating
    /// latitude and longitude separately would be wrong near a pole, where neighbouring samples can differ
    /// by up to 180 degrees of longitude and the straight line in (lat, lon) bends away from the real path.
    /// Returns the last point found visible, so the result is always on the globe.
    /// </summary>
    private static (double X, double Y) LimbCrossing(GlobeBasis basis, GeoCoordinate visible, GeoCoordinate hidden)
    {
        var (ax, ay, az) = UnitVector(visible.LatitudeDeg, visible.LongitudeDeg);
        var (bx, by, bz) = UnitVector(hidden.LatitudeDeg, hidden.LongitudeDeg);
        var omega = Math.Acos(Math.Clamp(ax * bx + ay * by + az * bz, -1.0, 1.0));
        var sinOmega = Math.Sin(omega);

        (double Lat, double Lon) At(double t)
        {
            // Spherical interpolation. For samples that are the same point there is no arc to walk.
            var wa = sinOmega < 1e-12 ? 1.0 : Math.Sin((1.0 - t) * omega) / sinOmega;
            var wb = sinOmega < 1e-12 ? 0.0 : Math.Sin(t * omega) / sinOmega;
            var x = wa * ax + wb * bx;
            var y = wa * ay + wb * by;
            var z = wa * az + wb * bz;
            return (Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180.0 / Math.PI, Math.Atan2(y, x) * 180.0 / Math.PI);
        }

        double lo = 0.0, hi = 1.0;
        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5 * (lo + hi);
            var (lat, lon) = At(mid);
            if (basis.Project(lat, lon).Visible)
                lo = mid;
            else
                hi = mid;
        }

        var (edgeLat, edgeLon) = At(lo);
        var edge = basis.Project(edgeLat, edgeLon);
        return (edge.X, edge.Y);
    }

    private static (double X, double Y, double Z) UnitVector(double latDeg, double lonDeg)
    {
        var lat = latDeg * Math.PI / 180.0;
        var lon = lonDeg * Math.PI / 180.0;
        return (Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>Great-circle destination from a start point, an angular distance and a bearing.</summary>
    public static (double LatDeg, double LonDeg) Destination(
        double latDeg, double lonDeg, double distanceDeg, double bearingDeg)
    {
        var lat = latDeg * Math.PI / 180.0;
        var lon = lonDeg * Math.PI / 180.0;
        var d = distanceDeg * Math.PI / 180.0;
        var b = bearingDeg * Math.PI / 180.0;

        var lat2 = Math.Asin(Math.Clamp(
            Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(b), -1.0, 1.0));
        var lon2 = lon + Math.Atan2(
            Math.Sin(b) * Math.Sin(d) * Math.Cos(lat),
            Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));

        var lonOutDeg = lon2 * 180.0 / Math.PI;
        lonOutDeg = (lonOutDeg + 540.0) % 360.0 - 180.0; // wrap to [-180, 180)
        return (lat2 * 180.0 / Math.PI, lonOutDeg);
    }
}
