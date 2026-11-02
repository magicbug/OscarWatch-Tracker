using OscarWatch.Core.Models;

namespace OscarWatch.Core.Geo;

/// <summary>A station drawn on the Earth View, with the label that is shown beside its marker.</summary>
public sealed record EarthViewStation(string Label, double LatitudeDeg, double LongitudeDeg, bool IsHome);

/// <summary>How the operator's station sits relative to the satellite's coverage.</summary>
public enum EarthViewSituation
{
    /// <summary>Outside the footprint: the satellite is below the station's horizon.</summary>
    BelowHorizon,

    /// <summary>Above the horizon, but below the configured minimum elevation.</summary>
    AboveHorizon,

    /// <summary>Above the configured minimum elevation: inside the inner ring.</summary>
    Usable
}

/// <summary>
/// Everything the Earth View needs to draw one moment, as a single immutable object so that one
/// property change redraws once. Built by <see cref="EarthViewFrameBuilder"/>.
/// </summary>
/// <param name="GroundTrack">The focused satellite's ground track, as supplied by the tracking service.</param>
/// <param name="MinimumElevationRadiusDeg">Angular radius of the inner footprint ring. Zero when there is none.</param>
/// <param name="Stations">Station markers. The first release lists the home station only.</param>
/// <param name="LookFromHome">Azimuth, elevation and range from the home station, when available.</param>
public sealed record EarthViewFrame(
    string NoradId,
    string SatelliteName,
    GlobeCamera Camera,
    DateTime DisplayUtc,
    IReadOnlyList<GeoCoordinate> GroundTrack,
    double MinimumElevationDeg,
    double MinimumElevationRadiusDeg,
    IReadOnlyList<EarthViewStation> Stations,
    GeoCoordinate Subsolar,
    bool IsSunlit,
    LookAngles? LookFromHome,
    EarthViewSituation HomeSituation)
{
    /// <summary>The subpoint must move by more than this many degrees before the drawing changes.</summary>
    public const double PositionToleranceDeg = 0.005;

    /// <summary>The altitude must change by more than this many kilometres before the drawing changes.</summary>
    public const double AltitudeToleranceKm = 0.5;

    /// <summary>The elevation must change by at least this many degrees for the accessible summary to change.</summary>
    public const double AccessibleElevationStepDeg = 1.0;

    /// <summary>
    /// True when this frame would look different from <paramref name="previous"/>. The view model uses
    /// this to avoid redrawing for changes too small to see, such as a geostationary satellite's drift.
    /// </summary>
    public bool NeedsRedraw(EarthViewFrame? previous)
    {
        if (previous is null)
            return true;

        return !string.Equals(NoradId, previous.NoradId, StringComparison.Ordinal)
            || !ReferenceEquals(GroundTrack, previous.GroundTrack)
            || Math.Abs(Camera.SubpointLatitudeDeg - previous.Camera.SubpointLatitudeDeg) > PositionToleranceDeg
            || Math.Abs(ShortestLongitudeDelta(Camera.SubpointLongitudeDeg, previous.Camera.SubpointLongitudeDeg)) > PositionToleranceDeg
            || Math.Abs(Camera.AltitudeKm - previous.Camera.AltitudeKm) > AltitudeToleranceKm
            || MinimumElevationDeg != previous.MinimumElevationDeg
            || IsSunlit != previous.IsSunlit
            || HomeSituation != previous.HomeSituation
            || !StationsEqual(Stations, previous.Stations)
            || FloorToMinute(DisplayUtc) != FloorToMinute(previous.DisplayUtc);
    }

    /// <summary>
    /// True when the spoken summary of this frame should replace the one for <paramref name="previous"/>:
    /// a different satellite, a change in how the station sits against the coverage, a new minimum
    /// elevation setting, or an elevation change of at least <see cref="AccessibleElevationStepDeg"/>. Ordinary movement does not count,
    /// so a screen reader is not given a new description on every redraw.
    /// </summary>
    public bool IsMaterialChange(EarthViewFrame? previous)
    {
        if (previous is null)
            return true;

        if (!string.Equals(NoradId, previous.NoradId, StringComparison.Ordinal)
            || HomeSituation != previous.HomeSituation
            || MinimumElevationDeg != previous.MinimumElevationDeg
            || (LookFromHome is null) != (previous.LookFromHome is null))
        {
            return true;
        }

        return LookFromHome is { } now
            && previous.LookFromHome is { } before
            && Math.Abs(now.ElevationDeg - before.ElevationDeg) >= AccessibleElevationStepDeg;
    }

    private static bool StationsEqual(IReadOnlyList<EarthViewStation> a, IReadOnlyList<EarthViewStation> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    private static double ShortestLongitudeDelta(double fromDeg, double toDeg)
    {
        var delta = (toDeg - fromDeg) % 360.0;
        if (delta > 180.0)
            delta -= 360.0;
        if (delta < -180.0)
            delta += 360.0;
        return delta;
    }

    private static long FloorToMinute(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;
}
