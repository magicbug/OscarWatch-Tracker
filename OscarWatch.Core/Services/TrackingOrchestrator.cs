using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Tle;

namespace OscarWatch.Core.Services;

public sealed class TrackingOrchestrator
{
    private readonly ISettingsService _settings;
    private readonly ITleService _tleService;
    private readonly IOrbitPropagator _propagator;
    private readonly IGroundGeometry _groundGeometry;
    private readonly IPassPredictor _passPredictor;
    private readonly SatelliteVisualCache _visualCache = new();

    public TrackingOrchestrator(
        ISettingsService settings,
        ITleService tleService,
        IOrbitPropagator propagator,
        IGroundGeometry groundGeometry,
        IPassPredictor passPredictor)
    {
        _settings = settings;
        _tleService = tleService;
        _propagator = propagator;
        _groundGeometry = groundGeometry;
        _passPredictor = passPredictor;
    }

    public void ReloadEnabledSatellites()
    {
        _propagator.Clear();
        _visualCache.Clear();
        foreach (var sat in _tleService.GetEnabledSatellites(_settings.Current))
            _propagator.LoadSatellite(sat);
    }

    /// <summary>Propagates all enabled satellites at <paramref name="utc"/>. UI should use <see cref="ILiveTrackingService"/>.</summary>
    public IReadOnlyList<SatelliteTrackState> GetLiveStates(DateTime utc)
    {
        var site = _settings.Current.GroundStation;
        var minEl = _settings.Current.MinimumElevationDeg;
        var sats = _tleService.GetEnabledSatellites(_settings.Current);
        var states = new List<SatelliteTrackState>();
        var sunEci = SunPositionCalculator.GetPosition(utc);

        foreach (var sat in sats)
        {
            if (!_propagator.HasSatellite(sat.NoradId))
                continue;

            try
            {
                LookAngles? look = null;
                try
                {
                    look = _propagator.GetLookAngles(sat.NoradId, site, utc);
                }
                catch
                {
                    // satellite may have decayed in TLE
                }

                var subpoint = _propagator.GetSubpoint(sat.NoradId, utc);
                var cache = _visualCache.GetOrAdd(sat.NoradId);
                var altKm = TleAltitude.ResolveAltitudeKm(subpoint.AltitudeKm, sat);

                if (!_visualCache.TryGetFreshGroundTrack(sat.NoradId, utc, out var groundTrack))
                {
                    var periodMin = EstimatePeriodMinutes(sat);
                    var halfPeriod = TimeSpan.FromMinutes(periodMin / 2.0);
                    groundTrack = _groundGeometry.GetGroundTrack(
                        sat, utc - halfPeriod, utc + halfPeriod, TimeSpan.FromSeconds(120));
                    cache.GroundTrack = groundTrack;
                    cache.GroundTrackUtc = utc;
                }

                if (!_visualCache.TryGetFreshFootprint(sat.NoradId, utc, out var footprint))
                {
                    footprint = _groundGeometry.GetFootprint(sat, utc, minimumElevationDeg: minEl);
                    cache.Footprint = footprint;
                    cache.FootprintUtc = utc;
                }

                // Always recompute: cheap formula keeps the radius in sync with the current
                // minimum-elevation setting and the satellite's current altitude.
                cache.FootprintRadiusDeg = FootprintGeometry.HorizonRadiusDeg(altKm, minEl);

                var footprintRadiusDeg = cache.FootprintRadiusDeg > 0
                    ? cache.FootprintRadiusDeg
                    : FootprintGeometry.EstimateRingRadiusDeg(subpoint, footprint);

                var satEci = _propagator.GetEciPosition(sat.NoradId, utc);
                var isSunlit = SatelliteIllumination.IsSunlit(satEci, sunEci);

                states.Add(new SatelliteTrackState
                {
                    Name = sat.Name,
                    NoradId = sat.NoradId,
                    Subpoint = subpoint,
                    LookAngles = look,
                    GroundTrack = groundTrack,
                    Footprint = footprint,
                    FootprintRadiusDeg = footprintRadiusDeg,
                    IsSunlit = isSunlit
                });
            }
            catch
            {
                // Skip satellites with bad TLEs without blocking the rest.
            }
        }

        return states;
    }

    /// <summary>Compass azimuth a few seconds ahead for rotator north-wrap lookahead.</summary>
    public double? TryGetAheadAzimuthDeg(string noradId, double secondsAhead = 3.0)
    {
        if (!_propagator.HasSatellite(noradId))
            return null;

        try
        {
            var look = _propagator.GetLookAngles(
                noradId,
                _settings.Current.GroundStation,
                DateTime.UtcNow.AddSeconds(secondsAhead));
            return look.AzimuthDeg;
        }
        catch
        {
            return null;
        }
    }

    public Task<IReadOnlyList<PassInfo>> GetUpcomingPassesAsync(CancellationToken cancellationToken = default)
    {
        var s = _settings.Current;
        return GetPassesAsync(
            s.GroundStation,
            s.MinimumElevationDeg,
            s.PassPredictionHours,
            s.PassFilterMinDurationMinutes,
            cancellationToken);
    }

    public async Task<IReadOnlyList<PassInfo>> GetPassesAsync(
        GroundStation site,
        double minimumElevationDeg,
        int predictionHours,
        int minimumDurationMinutes,
        CancellationToken cancellationToken = default)
    {
        var utcStart = DateTime.UtcNow;
        var utcEnd = utcStart.AddHours(predictionHours);
        var minDuration = TimeSpan.FromMinutes(Math.Max(0, minimumDurationMinutes));

        var allPasses = new List<PassInfo>();
        foreach (var sat in _tleService.GetEnabledSatellites(_settings.Current))
        {
            var passes = await _passPredictor.GetPassesAsync(
                sat, site, utcStart, utcEnd, minimumElevationDeg, cancellationToken);
            allPasses.AddRange(passes);
        }

        return allPasses
            .Where(p => p.Duration >= minDuration)
            .OrderBy(p => p.AosUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<MutualPassInfo>> GetMutualPassesAsync(
        GroundStation localSite,
        GroundStation remoteSite,
        double minimumElevationDeg,
        int predictionHours,
        int minimumPassDurationMinutes,
        int minimumMutualDurationMinutes,
        CancellationToken cancellationToken = default)
    {
        var utcStart = DateTime.UtcNow;
        var utcEnd = utcStart.AddHours(predictionHours);
        var minPassDuration = TimeSpan.FromMinutes(Math.Max(0, minimumPassDurationMinutes));
        var minMutualDuration = TimeSpan.FromMinutes(Math.Max(0, minimumMutualDurationMinutes));

        var localPasses = new List<PassInfo>();
        var remotePasses = new List<PassInfo>();

        foreach (var sat in _tleService.GetEnabledSatellites(_settings.Current))
        {
            var passes = await _passPredictor.GetPassesAsync(
                sat, localSite, utcStart, utcEnd, minimumElevationDeg, cancellationToken);
            localPasses.AddRange(passes.Where(p => p.Duration >= minPassDuration));

            passes = await _passPredictor.GetPassesAsync(
                sat, remoteSite, utcStart, utcEnd, minimumElevationDeg, cancellationToken);
            remotePasses.AddRange(passes.Where(p => p.Duration >= minPassDuration));
        }

        return MutualPassFinder.FindOverlaps(localPasses, remotePasses, minMutualDuration);
    }

    private static double EstimatePeriodMinutes(SatelliteCatalogEntry sat)
    {
        if (sat.Line2.Length < 52)
            return 90;
        try
        {
            var meanMotion = double.Parse(sat.Line2.AsSpan(52, 11));
            if (meanMotion > 0)
                return 1440.0 / meanMotion;
        }
        catch
        {
            // ignore
        }
        return 90;
    }
}
