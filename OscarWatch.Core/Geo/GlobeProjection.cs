namespace OscarWatch.Core.Geo;

/// <summary>Viewpoint above the Earth: the satellite's subpoint and altitude.</summary>
public readonly record struct GlobeCamera(
    double SubpointLatitudeDeg,
    double SubpointLongitudeDeg,
    double AltitudeKm);

/// <summary>
/// A point on the projection plane, in radians of arc from the subpoint. X is to the right (east at the
/// centre), Y is up (north at the centre), and the origin is the subpoint. The distance of the point from the
/// origin is its ground distance from the subpoint. <see cref="Visible"/> is false when the point is beyond
/// the limb.
/// </summary>
public readonly record struct GlobePoint(double X, double Y, bool Visible);

/// <summary>
/// Azimuthal equidistant projection of the Earth centred on a satellite's subpoint. A point's distance from
/// the centre of the picture is its ground distance from the subpoint, and its direction is the bearing from
/// the subpoint, so the whole footprint is spread evenly. Plane coordinates are in radians of arc.
/// </summary>
/// <remarks>
/// This replaced a true perspective view from the satellite. From a high orbit the perspective view
/// squeezes the outer part of the footprint into a thin rim: for QO-100, 41 percent of the ground in
/// the footprint (everything farther than 60 degrees from the subpoint) lands in the outer 14 percent of the
/// picture, so it looks much smaller than the footprint on the world map. Here the same ground gets 46 percent.
/// The price is that land near the edge is stretched sideways, by a factor of angle divided by sine of the
/// angle: 1.2 at 60 degrees from the centre and about 1.4 at the horizon of a geostationary satellite, and
/// almost nothing for a low orbit, whose horizon is only about 20 degrees away.
/// <para>
/// The distance of the camera from the Earth's centre (<see cref="Ratio"/>) is still needed to find the
/// horizon, which is where the footprint ends: cos(horizon angle) = 1 / ratio.
/// </para>
/// </remarks>
public static class GlobeProjection
{
    public const double EarthRadiusKm = 6371.0;

    /// <summary>Satellite distance from the Earth's centre as a multiple of the Earth radius (P).</summary>
    public static double Ratio(double altitudeKm) => (EarthRadiusKm + altitudeKm) / EarthRadiusKm;

    /// <summary>
    /// Radius of the limb circle on the projection plane: the horizon angle, acos(R / (R + h)), in radians.
    /// The limb is the edge of the footprint at 0 degrees elevation. Zero when the altitude is not a finite
    /// positive number.
    /// </summary>
    public static double LimbRadius(double altitudeKm)
    {
        if (!double.IsFinite(altitudeKm) || altitudeKm <= 0)
            return 0;

        return Math.Acos(1.0 / Ratio(altitudeKm));
    }

    /// <summary>
    /// Radius, on the projection plane in radians, of the circle of points that lie
    /// <paramref name="centralAngleDeg"/> degrees of arc from the subpoint. That is simply the angle itself, so
    /// rings like the minimum elevation footprint are exact circles and can be drawn as ellipses. Angles beyond
    /// the horizon give the limb radius. Zero when the altitude or the angle is not a usable number.
    /// </summary>
    public static double RingRadius(double altitudeKm, double centralAngleDeg)
    {
        if (!double.IsFinite(altitudeKm) || altitudeKm <= 0 || !double.IsFinite(centralAngleDeg) || centralAngleDeg <= 0)
            return 0;

        // Distance from the centre is simply the angle itself.
        return Math.Min(centralAngleDeg * Math.PI / 180.0, LimbRadius(altitudeKm));
    }

    /// <summary>Projects a geographic point. See <see cref="GlobeBasis"/> to project many points.</summary>
    public static GlobePoint Project(GlobeCamera camera, double latDeg, double lonDeg) =>
        new GlobeBasis(camera).Project(latDeg, lonDeg);

    /// <summary>
    /// Inverse projection. Returns false when the plane point misses the globe, or when the camera
    /// altitude is not a finite positive number.
    /// </summary>
    public static bool TryUnproject(
        GlobeCamera camera, double x, double y, out double latDeg, out double lonDeg) =>
        new GlobeBasis(camera).TryUnproject(x, y, out latDeg, out lonDeg);
}

/// <summary>
/// Precomputed trigonometry for one satellite position, so that many points (a ground track, or every pixel of
/// a raster) can be projected without repeating it. Local axes: X east, Y north, Z up (towards the
/// satellite) at the subpoint.
/// </summary>
public readonly struct GlobeBasis
{
    private readonly double _sinLat0;
    private readonly double _cosLat0;
    private readonly double _lon0Rad;
    private readonly double _p;
    private readonly double _horizonRad;

    // Geographic (Earth-fixed) unit vectors of the local axes.
    private readonly double _eastX, _eastY;
    private readonly double _northX, _northY, _northZ;
    private readonly double _upX, _upY, _upZ;

    public GlobeBasis(GlobeCamera camera)
    {
        var valid = double.IsFinite(camera.SubpointLatitudeDeg)
            && double.IsFinite(camera.SubpointLongitudeDeg)
            && double.IsFinite(camera.AltitudeKm)
            && camera.AltitudeKm > 0;

        IsValid = valid;
        if (!valid)
        {
            _sinLat0 = _cosLat0 = _lon0Rad = _p = _horizonRad = 0;
            _eastX = _eastY = _northX = _northY = _northZ = _upX = _upY = _upZ = 0;
            return;
        }

        var lat0 = camera.SubpointLatitudeDeg * Math.PI / 180.0;
        _lon0Rad = camera.SubpointLongitudeDeg * Math.PI / 180.0;
        _sinLat0 = Math.Sin(lat0);
        _cosLat0 = Math.Cos(lat0);
        _p = GlobeProjection.Ratio(camera.AltitudeKm);
        _horizonRad = Math.Acos(1.0 / _p);

        var sinLon0 = Math.Sin(_lon0Rad);
        var cosLon0 = Math.Cos(_lon0Rad);
        _eastX = -sinLon0;
        _eastY = cosLon0;
        _northX = -_sinLat0 * cosLon0;
        _northY = -_sinLat0 * sinLon0;
        _northZ = _cosLat0;
        _upX = _cosLat0 * cosLon0;
        _upY = _cosLat0 * sinLon0;
        _upZ = _sinLat0;
    }

    /// <summary>False when the camera altitude or position is not usable. Every method then reports a miss.</summary>
    public bool IsValid { get; }

    /// <summary>Radius of the limb circle on the projection plane: the horizon angle in radians.</summary>
    public double LimbRadius => IsValid ? _horizonRad : 0;

    /// <summary>
    /// Converts a geographic position to the unit vector towards it in local axes (X east, Y north,
    /// Z up at the subpoint). The Z component is the cosine of the central angle from the subpoint.
    /// Also used to express the direction of the sun in the same axes as the surface normals.
    /// All three are zero when the camera is not valid.
    /// </summary>
    public void GeoToLocal(double latDeg, double lonDeg, out double lx, out double ly, out double lz)
    {
        if (!IsValid)
        {
            lx = ly = lz = 0;
            return;
        }

        var lat = latDeg * Math.PI / 180.0;
        var dLon = lonDeg * Math.PI / 180.0 - _lon0Rad;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var cosD = Math.Cos(dLon);

        lx = cosLat * Math.Sin(dLon);
        ly = _cosLat0 * sinLat - _sinLat0 * cosLat * cosD;
        lz = _sinLat0 * sinLat + _cosLat0 * cosLat * cosD;
    }

    public GlobePoint Project(double latDeg, double lonDeg)
    {
        if (!IsValid)
            return new GlobePoint(0, 0, false);

        GeoToLocal(latDeg, lonDeg, out var lx, out var ly, out var lz);

        // Visible when the point is above the satellite's horizon: cos c >= R / (R + h).
        // Written so that NaN input reports not visible.
        var visible = lz >= 1.0 / _p;

        // Azimuthal equidistant: keep the direction, make the distance from the centre the angle c.
        var sinC = Math.Sqrt(lx * lx + ly * ly);
        if (sinC < 1e-12)
            return new GlobePoint(0, 0, visible);

        var k = Math.Atan2(sinC, lz) / sinC;
        return new GlobePoint(k * lx, k * ly, visible);
    }

    /// <summary>
    /// Finds the point on the globe that a plane point shows. The plane point's distance from the centre is
    /// the angle c from the subpoint, and its direction is the bearing. The hit is returned as a unit vector in
    /// local axes (X east, Y north, Z up), which is also the surface normal, so callers can light it directly.
    /// Returns false beyond the limb.
    /// </summary>
    public bool TryHitLocal(double x, double y, out double hitX, out double hitY, out double hitZ)
    {
        hitX = hitY = hitZ = 0;
        if (!IsValid || !double.IsFinite(x) || !double.IsFinite(y))
            return false;

        var rho = Math.Sqrt(x * x + y * y);
        if (rho > _horizonRad)
            return false;

        if (rho < 1e-12)
        {
            hitZ = 1.0;
            return true;
        }

        var scale = Math.Sin(rho) / rho;
        hitX = x * scale;
        hitY = y * scale;
        hitZ = Math.Cos(rho);
        return true;
    }

    /// <summary>Converts a unit vector in local axes to latitude and longitude, in degrees.</summary>
    public void LocalToGeo(double lx, double ly, double lz, out double latDeg, out double lonDeg)
    {
        var vx = lx * _eastX + ly * _northX + lz * _upX;
        var vy = lx * _eastY + ly * _northY + lz * _upY;
        var vz = ly * _northZ + lz * _upZ;

        latDeg = Math.Asin(Math.Clamp(vz, -1.0, 1.0)) * 180.0 / Math.PI;

        var lon = Math.Atan2(vy, vx) * 180.0 / Math.PI;
        lonDeg = lon <= -180.0 ? lon + 360.0 : lon;
    }

    public bool TryUnproject(double x, double y, out double latDeg, out double lonDeg)
    {
        latDeg = lonDeg = 0;
        if (!TryHitLocal(x, y, out var hx, out var hy, out var hz))
            return false;

        LocalToGeo(hx, hy, hz, out latDeg, out lonDeg);
        return true;
    }
}
