using OscarWatch.Core.Models;

namespace OscarWatch.Core.Geo;

/// <summary>Turns a tracking state into an <see cref="EarthViewFrame"/>. Pure, so it is easy to test.</summary>
public static class EarthViewFrameBuilder
{
    /// <summary>
    /// Builds a frame, or returns null when the state cannot be drawn: no usable altitude (the
    /// horizon angle, which sets the size of the footprint, needs it) or a subpoint that is not a finite position.
    /// </summary>
    /// <remarks>
    /// "Below horizon" and "inside the ring" are decided from the same footprint geometry that the
    /// drawing uses, so the text always agrees with the picture. They use the plain minimum elevation
    /// and ignore any horizon mask, which a circular ring cannot show.
    /// </remarks>
    public static EarthViewFrame? Build(
        SatelliteTrackState state,
        GroundStation home,
        double minimumElevationDeg,
        DateTime displayUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(home);

        if (!double.IsFinite(state.AltitudeKm) || state.AltitudeKm <= 0
            || !double.IsFinite(state.Subpoint.LatitudeDeg) || !double.IsFinite(state.Subpoint.LongitudeDeg))
        {
            return null;
        }

        var minElevation = double.IsFinite(minimumElevationDeg) ? Math.Clamp(minimumElevationDeg, 0.0, 90.0) : 0.0;
        var camera = new GlobeCamera(state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, state.AltitudeKm);

        var horizonRadiusDeg = GlobeViewMath.FootprintRadiusDeg(state.AltitudeKm, 0.0);
        var ringRadiusDeg = GlobeViewMath.FootprintRadiusDeg(state.AltitudeKm, minElevation);

        var situation = EarthViewSituation.BelowHorizon;
        if (GlobeViewMath.IsInsideFootprint(state.Subpoint, home.LatitudeDeg, home.LongitudeDeg, horizonRadiusDeg))
        {
            situation = GlobeViewMath.IsInsideFootprint(state.Subpoint, home.LatitudeDeg, home.LongitudeDeg, ringRadiusDeg)
                ? EarthViewSituation.Usable
                : EarthViewSituation.AboveHorizon;
        }

        return new EarthViewFrame(
            NoradId: state.NoradId,
            SatelliteName: state.Name,
            Camera: camera,
            DisplayUtc: displayUtc,
            GroundTrack: state.GroundTrack,
            MinimumElevationDeg: minElevation,
            MinimumElevationRadiusDeg: ringRadiusDeg,
            Stations: [new EarthViewStation(HomeLabel(home), home.LatitudeDeg, home.LongitudeDeg, IsHome: true)],
            Subsolar: DayNightTerminator.GetSubsolarPoint(displayUtc),
            IsSunlit: state.IsSunlit,
            LookFromHome: state.LookAngles,
            HomeSituation: situation);
    }

    /// <summary>
    /// The station's callsign, else its name, else empty. An empty label means the UI shows its own
    /// localised word for "station", so no English text is built in here.
    /// </summary>
    private static string HomeLabel(GroundStation home)
    {
        if (!string.IsNullOrWhiteSpace(home.Callsign))
            return home.Callsign.Trim();

        return string.IsNullOrWhiteSpace(home.DisplayName) ? "" : home.DisplayName.Trim();
    }
}
