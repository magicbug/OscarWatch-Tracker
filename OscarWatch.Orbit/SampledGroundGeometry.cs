using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Tle;

namespace OscarWatch.Orbit;

public sealed class SampledGroundGeometry : IGroundGeometry
{
    private readonly IOrbitPropagator _propagator;

    public SampledGroundGeometry(IOrbitPropagator propagator)
    {
        _propagator = propagator;
    }

    public IReadOnlyList<GeoCoordinate> GetGroundTrack(
        SatelliteCatalogEntry satellite,
        DateTime utcStart,
        DateTime utcEnd,
        TimeSpan step)
    {
        var usePropagator = _propagator.HasSatellite(satellite.NoradId);
        var orbit = usePropagator ? null : OrbitToolsMapping.CreateOrbit(satellite);

        // Guard against invalid step values
        if (step <= TimeSpan.Zero)
            return [];

        // Handle case where utcEnd is before utcStart (return empty like old implementation)
        if (utcEnd < utcStart)
            return [];

        // Optimized: Pre-calculate step count to avoid reallocations
        var timeSpan = utcEnd - utcStart;
        var stepCount = (int)(timeSpan.Ticks / step.Ticks) + 1;
        var rawPoints = new List<GeoCoordinate?>(stepCount);

        // Optimized: Use for-loop instead of while-loop to avoid DateTime addition in condition
        // Preserve original DateTimeKind from utcStart instead of forcing UTC
        var stepTicks = step.Ticks;
        var startTicks = utcStart.Ticks;
        var endTicks = utcEnd.Ticks;
        var originalKind = utcStart.Kind;

        for (var tickOffset = 0L; startTicks + tickOffset <= endTicks; tickOffset += stepTicks)
        {
            var t = new DateTime(startTicks + tickOffset, originalKind);
            try
            {
                var point = usePropagator
                    ? _propagator.GetSubpoint(satellite.NoradId, t)
                    : OrbitToolsMapping.ToGeoCoordinate(orbit!.PositionEci(t));
                rawPoints.Add(point);
            }
            catch
            {
                rawPoints.Add(null);
            }
        }

        // Optimized: Pre-allocate final list with estimated capacity (most points will be valid)
        var points = new List<GeoCoordinate>(rawPoints.Count);
        var i = 0;

        while (i < rawPoints.Count)
        {
            if (rawPoints[i] is not null)
            {
                points.Add(rawPoints[i]!);
                i++;
            }
            else
            {
                // Count consecutive failures
                var gapStart = i;
                while (i < rawPoints.Count && rawPoints[i] is null)
                    i++;

                var gapLength = i - gapStart;

                if (gapLength == 1)
                {
                    // Single missing point: interpolate if both neighbours exist
                    var prev = gapStart > 0 ? rawPoints[gapStart - 1] : null;
                    var next = i < rawPoints.Count ? rawPoints[i] : null;

                    if (prev is not null && next is not null)
                    {
                        // Linear interpolation of lat/lon/alt
                        var interpLat = (prev.LatitudeDeg + next.LatitudeDeg) / 2.0;
                        var interpLon = (prev.LongitudeDeg + next.LongitudeDeg) / 2.0;
                        var interpAlt = (prev.AltitudeKm + next.AltitudeKm) / 2.0;
                        points.Add(new GeoCoordinate(interpLat, interpLon, interpAlt));
                    }
                    else
                    {
                        // No previous success (first point fails) or next also fails:
                        // insert a NaN sentinel
                        points.Add(new GeoCoordinate(double.NaN, double.NaN));
                    }
                }
                else
                {
                    // Consecutive missing points (≥2): insert a single NaN sentinel
                    points.Add(new GeoCoordinate(double.NaN, double.NaN));
                }
            }
        }

        return points;
    }

    public IReadOnlyList<GeoCoordinate> GetFootprint(
        SatelliteCatalogEntry satellite,
        DateTime utc,
        double minimumElevationDeg)
    {
        var usePropagator = _propagator.HasSatellite(satellite.NoradId);

        GeoCoordinate subpoint;
        try
        {
            subpoint = usePropagator
                ? _propagator.GetSubpoint(satellite.NoradId, utc)
                : OrbitToolsMapping.ToGeoCoordinate(
                    OrbitToolsMapping.CreateOrbit(satellite).PositionEci(utc));
        }
        catch
        {
            return [];
        }

        var altKm = TleAltitude.ResolveAltitudeKm(subpoint.AltitudeKm, satellite);
        var radiusDeg = FootprintGeometry.HorizonRadiusDeg(altKm, minimumElevationDeg);
        if (radiusDeg <= 0)
            return [];
        const int samples = 90;
        var ring = new List<GeoCoordinate>(samples);

        for (var i = 0; i < samples; i++)
        {
            var bearing = i * 360.0 / samples;
            var (lat, lon) = SphericalGeo.DestinationPoint(
                subpoint.LatitudeDeg,
                subpoint.LongitudeDeg,
                radiusDeg,
                bearing);
            ring.Add(new GeoCoordinate(lat, lon));

        }

        return ring;
    }
}
