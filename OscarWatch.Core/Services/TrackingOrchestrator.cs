using System.Diagnostics;
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
    private readonly ISunPositionCalculator _sunCalculator;
    private readonly ISatelliteDatabaseService? _satelliteDatabase;
    private readonly ITrackingDiagnostics _diagnostics;
    private readonly SatelliteVisualCache _visualCache = new();
    private readonly HashSet<string> _loggedLookAngleSkips = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedStateSkips = new(StringComparer.Ordinal);
    private List<SatelliteCatalogEntry> _cachedEnabledSats = new();
    private int _lastNonFocusedRecomputeIndex;

    // Sun position cache: sun moves ~0.004°/min, so 30-second cache is very effective
    // Note: If map time is scrubbed by less than 30s, illumination uses a stale sun position.
    // This is fine for live tracking; acceptable for scrub scenarios where performance matters.
    private EciPosition _cachedSunPosition;
    private DateTime _cachedSunPositionUtc = DateTime.MinValue;
    private static readonly TimeSpan SunCacheValidDuration = TimeSpan.FromSeconds(30);

    private List<SatelliteTrackState> _bufferA = new(32);
    private List<SatelliteTrackState> _bufferB = new(32);
    private bool _useBufferA = true;

    public TrackingOrchestrator(
        ISettingsService settings,
        ITleService tleService,
        IOrbitPropagator propagator,
        IGroundGeometry groundGeometry,
        IPassPredictor passPredictor,
        ITrackingDiagnostics? diagnostics = null,
        ISatelliteDatabaseService? satelliteDatabase = null,
        ISunPositionCalculator? sunCalculator = null)
    {
        _settings = settings;
        _tleService = tleService;
        _propagator = propagator;
        _groundGeometry = groundGeometry;
        _passPredictor = passPredictor;
        _diagnostics = diagnostics ?? NullTrackingDiagnostics.Instance;
        _satelliteDatabase = satelliteDatabase;
        _sunCalculator = sunCalculator ?? DefaultSunPositionCalculator.Instance;
    }

    public void ReloadEnabledSatellites()
    {
        _propagator.Clear();
        _visualCache.Clear();
        _loggedLookAngleSkips.Clear();
        _loggedStateSkips.Clear();
        _bufferA.Clear();
        _bufferB.Clear();
        var sats = _tleService.GetEnabledSatellites(_settings.Current);
        _cachedEnabledSats = new List<SatelliteCatalogEntry>(sats);
        foreach (var sat in sats)
            _propagator.LoadSatellite(sat);
    }

    /// <summary>
    /// Adds a single satellite to the propagator and enabled list without clearing existing state.
    /// </summary>
    public void AddSatellite(SatelliteCatalogEntry satellite)
    {
        if (_propagator.HasSatellite(satellite.NoradId))
            return; // Already loaded

        _propagator.LoadSatellite(satellite);
        _cachedEnabledSats.Add(satellite);
    }

    /// <summary>
    /// Removes a single satellite from the propagator, visual cache, and enabled list without clearing other state.
    /// </summary>
    public void RemoveSatellite(string noradId)
    {
        _propagator.RemoveSatellite(noradId);
        _visualCache.Remove(noradId);
        _loggedLookAngleSkips.Remove(noradId);
        _loggedStateSkips.Remove(noradId);
        
        // Optimized: in-place removal instead of LINQ allocation
        for (int i = _cachedEnabledSats.Count - 1; i >= 0; i--)
        {
            if (_cachedEnabledSats[i].NoradId == noradId)
            {
                _cachedEnabledSats.RemoveAt(i);
                break; // Assuming unique NORAD IDs
            }
        }
    }

    /// <summary>Clears cached ground tracks and footprints (e.g. after map-time scrub).</summary>
    public void InvalidateVisualCache() => _visualCache.Clear();

    /// <summary>Gets sun position with caching. Sun moves ~0.004°/min so 30-second cache is effective.</summary>
    private EciPosition GetCachedSunPosition(DateTime utc)
    {
        if (_cachedSunPositionUtc == DateTime.MinValue || Math.Abs((utc - _cachedSunPositionUtc).TotalSeconds) > SunCacheValidDuration.TotalSeconds)
        {
            _cachedSunPosition = _sunCalculator.GetPosition(utc);
            _cachedSunPositionUtc = utc;
        }
        return _cachedSunPosition;
    }

    /// <summary>Propagates all enabled satellites at <paramref name="utc"/>. UI should use <see cref="ILiveTrackingService"/>.</summary>
    /// <param name="groundTrackNoradId">When set, ground track geometry is computed only for this NORAD id (map focus).</param>
    public IReadOnlyList<SatelliteTrackState> GetLiveStates(DateTime utc, string? groundTrackNoradId = null)
    {
        var site = _settings.Current.GroundStation;
        var sats = _cachedEnabledSats;
        var states = _useBufferA ? _bufferB : _bufferA;
        states.Clear();
        var sunEci = GetCachedSunPosition(utc);

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
                catch (Exception ex)
                {
                    if (_loggedLookAngleSkips.Add(sat.NoradId))
                        _diagnostics.LookAnglesSkipped(sat.NoradId, utc, ex);
                }

                var subpoint = _propagator.GetSubpoint(sat.NoradId, utc);
                var cache = _visualCache.GetOrAdd(sat.NoradId);

                double? motionHeadingDeg;
                if (_visualCache.TryGetFreshMotionHeading(sat.NoradId, utc, out var cachedHeading))
                {
                    motionHeadingDeg = cachedHeading;
                }
                else
                {
                    motionHeadingDeg = TryEstimateMotionHeadingDeg(sat.NoradId, utc, subpoint);
                    cache.MotionHeadingDeg = motionHeadingDeg;
                    cache.MotionHeadingUtc = utc;
                }

                var altKm = TleAltitude.ResolveAltitudeKm(subpoint.AltitudeKm, sat);

                IReadOnlyList<GeoCoordinate> groundTrack = [];
                IReadOnlyList<GeoCoordinate> nextOrbitGroundTrack = [];
                var isFocusedTrack = string.Equals(sat.NoradId, groundTrackNoradId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(groundTrackNoradId);
                if (!_visualCache.TryGetFreshGroundTrack(sat.NoradId, utc, isFocusedTrack, out groundTrack))
                {
                    if (isFocusedTrack)
                    {
                        // Focused satellite: always recompute immediately
                        var periodMin = EstimatePeriodMinutes(sat);
                        var halfPeriod = TimeSpan.FromMinutes(periodMin / 2.0);
                        groundTrack = _groundGeometry.GetGroundTrack(
                            sat, utc - halfPeriod, utc + halfPeriod, TimeSpan.FromSeconds(60));
                        cache.GroundTrack = groundTrack;
                        cache.GroundTrackUtc = utc;

                        // Compute next orbit track (one period ahead) for overlay
                        var period = TimeSpan.FromMinutes(periodMin);
                        nextOrbitGroundTrack = _groundGeometry.GetGroundTrack(
                            sat, utc + halfPeriod, utc + halfPeriod + period, TimeSpan.FromSeconds(120));
                        cache.NextOrbitGroundTrack = nextOrbitGroundTrack;
                    }
                    else
                    {
                        // Non-focused: use stale cache as placeholder; recompute below with stagger
                        groundTrack = cache.GroundTrack;
                    }
                }
                else if (isFocusedTrack)
                {
                    nextOrbitGroundTrack = cache.NextOrbitGroundTrack;
                    if (nextOrbitGroundTrack.Count < 2)
                    {
                        // Next orbit not yet computed — compute it now
                        var periodMin = EstimatePeriodMinutes(sat);
                        var halfPeriod = TimeSpan.FromMinutes(periodMin / 2.0);
                        var period = TimeSpan.FromMinutes(periodMin);
                        nextOrbitGroundTrack = _groundGeometry.GetGroundTrack(
                            sat, utc + halfPeriod, utc + halfPeriod + period, TimeSpan.FromSeconds(120));
                        cache.NextOrbitGroundTrack = nextOrbitGroundTrack;
                    }
                }

                if (!_visualCache.TryGetFreshFootprint(sat.NoradId, utc, out var footprint))
                {
                    footprint = _groundGeometry.GetFootprint(sat, utc, minimumElevationDeg: 0);
                    cache.Footprint = footprint;
                    cache.FootprintUtc = utc;
                    cache.FootprintRadiusDeg = FootprintGeometry.HorizonRadiusDeg(altKm, minimumElevationDeg: 0);
                }
                else if (cache.FootprintRadiusDeg <= 0)
                {
                    cache.FootprintRadiusDeg = FootprintGeometry.HorizonRadiusDeg(altKm, minimumElevationDeg: 0);
                }

                var footprintRadiusDeg = cache.FootprintRadiusDeg > 0
                    ? cache.FootprintRadiusDeg
                    : FootprintGeometry.EstimateRingRadiusDeg(subpoint, footprint);

                var satEci = _propagator.GetEciPosition(sat.NoradId, utc);
                var isSunlit = SatelliteIllumination.IsSunlit(satEci, sunEci);
                states.Add(new SatelliteTrackState
                {
                    Name = ResolveDisplayName(sat),
                    NoradId = sat.NoradId,
                    Subpoint = subpoint,
                    LookAngles = look,
                    MotionHeadingDeg = motionHeadingDeg,
                    GroundTrack = groundTrack,
                    NextOrbitGroundTrack = nextOrbitGroundTrack,
                    Footprint = footprint,
                    FootprintRadiusDeg = footprintRadiusDeg,
                    IsSunlit = isSunlit
                });
            }
            catch (Exception ex)
            {
                if (_loggedStateSkips.Add(sat.NoradId))
                    _diagnostics.SatelliteStateSkipped(sat.NoradId, utc, ex);
            }
        }

        // Staggered non-focused ground track recomputation: max 2 per tick, 20ms timeout
        var nonFocusedStale = new List<(SatelliteCatalogEntry Sat, SatelliteVisualCache.Entry Cache)>();
        foreach (var sat in sats)
        {
            if (!_propagator.HasSatellite(sat.NoradId))
                continue;
            if (string.Equals(sat.NoradId, groundTrackNoradId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(groundTrackNoradId))
                continue;
            if (!_visualCache.TryGetFreshGroundTrack(sat.NoradId, utc, isFocused: false, out _))
                nonFocusedStale.Add((sat, _visualCache.GetOrAdd(sat.NoradId)));
        }

        if (nonFocusedStale.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var recomputedCount = 0;
            var startIndex = _lastNonFocusedRecomputeIndex % nonFocusedStale.Count;

            for (var i = 0; i < nonFocusedStale.Count && recomputedCount < 2; i++)
            {
                if (sw.ElapsedMilliseconds >= 20)
                    break;

                var idx = (startIndex + i) % nonFocusedStale.Count;
                var (staleSat, staleCache) = nonFocusedStale[idx];

                var periodMin = EstimatePeriodMinutes(staleSat);
                var halfPeriod = TimeSpan.FromMinutes(periodMin / 2.0);
                var track = _groundGeometry.GetGroundTrack(
                    staleSat, utc - halfPeriod, utc + halfPeriod, TimeSpan.FromSeconds(120));
                staleCache.GroundTrack = track;
                staleCache.GroundTrackUtc = utc;

                // Update the corresponding state in the buffer
                for (var si = 0; si < states.Count; si++)
                {
                    if (states[si].NoradId == staleSat.NoradId)
                    {
                        var s = states[si];
                        states[si] = new SatelliteTrackState
                        {
                            Name = s.Name,
                            NoradId = s.NoradId,
                            Subpoint = s.Subpoint,
                            LookAngles = s.LookAngles,
                            MotionHeadingDeg = s.MotionHeadingDeg,
                            GroundTrack = track,
                            Footprint = s.Footprint,
                            FootprintRadiusDeg = s.FootprintRadiusDeg,
                            IsSunlit = s.IsSunlit
                        };
                        break;
                    }
                }

                recomputedCount++;
            }

            _lastNonFocusedRecomputeIndex = (startIndex + recomputedCount) % Math.Max(1, nonFocusedStale.Count);
        }

        _useBufferA = !_useBufferA;
        return states;
    }

    private static bool ShouldBuildGroundTrack(string noradId, string? groundTrackNoradId) =>
        true; // Always build ground tracks for all satellites (was: only for focused)

    private double? TryEstimateMotionHeadingDeg(string noradId, DateTime utc, GeoCoordinate subpoint)
    {
        try
        {
            var ahead = _propagator.GetSubpoint(noradId, utc.AddSeconds(45));
            return SphericalGeo.InitialBearingDeg(
                subpoint.LatitudeDeg,
                subpoint.LongitudeDeg,
                ahead.LatitudeDeg,
                EquirectangularProjection.NormalizeLongitudeNear(ahead.LongitudeDeg, subpoint.LongitudeDeg));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compass azimuth a few seconds ahead for rotator north-wrap lookahead.</summary>
    public IReadOnlyList<SkyPlotPathPoint> BuildSkyPlotPassPath(PassInfo pass, GroundStation site)
    {
        if (!_propagator.HasSatellite(pass.NoradId))
            return [];

        return SkyPlotPathBuilder.Build(pass, _propagator, site, _settings.Current.MinimumElevationDeg);
    }

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
        catch (Exception ex)
        {
            if (_loggedLookAngleSkips.Add(noradId))
                _diagnostics.LookAnglesSkipped(noradId, DateTime.UtcNow.AddSeconds(secondsAhead), ex);
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

        var sats = _tleService.GetEnabledSatellites(_settings.Current);
        var tasks = sats.Select(sat =>
            _passPredictor.GetPassesAsync(sat, site, utcStart, utcEnd, minimumElevationDeg, cancellationToken))
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Allow partial results to be collected below.
        }

        return tasks
            .Where(t => t.IsCompletedSuccessfully)
            .SelectMany(t => t.Result)
            .Where(p => p.Duration >= minDuration)
            .Select(ApplyDisplayName)
            .OrderBy(p => p.AosUtc)
            .ToList();
    }

    public Task<IReadOnlyList<MutualPassInfo>> GetMutualPassesAsync(
        GroundStation localSite,
        GroundStation remoteSite,
        double minimumElevationDeg,
        int predictionHours,
        int minimumPassDurationMinutes,
        int minimumMutualDurationMinutes,
        CancellationToken cancellationToken = default)
    {
        var utcStart = DateTime.UtcNow;
        return GetMutualPassesAsync(
            localSite,
            remoteSite,
            minimumElevationDeg,
            utcStart,
            utcStart.AddHours(predictionHours),
            minimumPassDurationMinutes,
            minimumMutualDurationMinutes,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MutualPassInfo>> GetMutualPassesAsync(
        GroundStation localSite,
        GroundStation remoteSite,
        double minimumElevationDeg,
        DateTime utcStart,
        DateTime utcEnd,
        int minimumPassDurationMinutes,
        int minimumMutualDurationMinutes,
        CancellationToken cancellationToken = default)
    {
        var minPassDuration = TimeSpan.FromMinutes(Math.Max(0, minimumPassDurationMinutes));
        var minMutualDuration = TimeSpan.FromMinutes(Math.Max(0, minimumMutualDurationMinutes));

        var sats = _tleService.GetEnabledSatellites(_settings.Current);

        var localTasks = sats.Select(sat =>
            _passPredictor.GetPassesAsync(sat, localSite, utcStart, utcEnd, minimumElevationDeg, cancellationToken))
            .ToList();
        var remoteTasks = sats.Select(sat =>
            _passPredictor.GetPassesAsync(sat, remoteSite, utcStart, utcEnd, minimumElevationDeg, cancellationToken))
            .ToList();

        var allTasks = localTasks.Concat(remoteTasks).ToList();
        try
        {
            await Task.WhenAll(allTasks);
        }
        catch
        {
            // Allow partial results to be collected below
        }

        var localPasses = localTasks
            .Where(t => t.IsCompletedSuccessfully)
            .SelectMany(t => t.Result)
            .Where(p => p.Duration >= minPassDuration)
            .Select(ApplyDisplayName)
            .ToList();

        var remotePasses = remoteTasks
            .Where(t => t.IsCompletedSuccessfully)
            .SelectMany(t => t.Result)
            .Where(p => p.Duration >= minPassDuration)
            .Select(ApplyDisplayName)
            .ToList();

        return MutualPassFinder.FindOverlaps(localPasses, remotePasses, minMutualDuration)
            .Select(ApplyDisplayName)
            .ToList();
    }

    private string ResolveDisplayName(SatelliteCatalogEntry sat) =>
        SatelliteDisplayName.Resolve(sat.Name, sat.NoradId, _satelliteDatabase);

    private PassInfo ApplyDisplayName(PassInfo pass)
    {
        pass.SatelliteName = SatelliteDisplayName.Resolve(pass.SatelliteName, pass.NoradId, _satelliteDatabase);
        return pass;
    }

    private MutualPassInfo ApplyDisplayName(MutualPassInfo pass)
    {
        pass.SatelliteName = SatelliteDisplayName.Resolve(pass.SatelliteName, pass.NoradId, _satelliteDatabase);
        ApplyDisplayName(pass.LocalPass);
        ApplyDisplayName(pass.RemotePass);
        return pass;
    }

    private static double EstimatePeriodMinutes(SatelliteCatalogEntry sat)
    {
        if (!TleOrbitalSanity.TryReadLine2Elements(sat.Line2, out _, out _, out var meanMotion)
            || meanMotion <= 0)
            return 90;

        return 1440.0 / meanMotion;
    }
}
