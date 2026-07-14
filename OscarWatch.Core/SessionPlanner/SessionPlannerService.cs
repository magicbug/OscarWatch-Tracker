using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Orchestrates session plan generation: predicts passes, scores them,
/// and selects the optimal non-overlapping subset via weighted interval scheduling.
/// </summary>
public sealed class SessionPlannerService
{
    private static readonly TimeSpan MaxSessionDuration = TimeSpan.FromHours(48);

    private readonly TrackingOrchestrator _orchestrator;
    private readonly ISatelliteDatabaseService _satelliteDb;
    private readonly ISettingsService _settings;

    public SessionPlannerService(
        TrackingOrchestrator orchestrator,
        ISatelliteDatabaseService satelliteDb,
        ISettingsService settings)
    {
        _orchestrator = orchestrator;
        _satelliteDb = satelliteDb;
        _settings = settings;
    }

    /// <summary>
    /// Generates a session plan for the given time window.
    /// Validates the window, predicts passes, scores each candidate,
    /// and selects the optimal non-overlapping subset.
    /// </summary>
    public async Task<SessionPlan> GeneratePlanAsync(
        DateTime sessionStartUtc,
        DateTime sessionEndUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionWindow(sessionStartUtc, sessionEndUtc);

        var settings = _settings.Current;
        var site = settings.GroundStation;
        var minElevation = settings.MinimumElevationDeg;
        // Prediction must cover from now to the end of the session window,
        // since GetPassesAsync starts at UtcNow regardless of session start.
        var hoursFromNowToEnd = (int)Math.Ceiling((sessionEndUtc - DateTime.UtcNow).TotalHours);
        var predictionHours = Math.Max(1, hoursFromNowToEnd);
        var minDurationMinutes = settings.PassFilterMinDurationMinutes;

        var allPasses = await _orchestrator.GetPassesAsync(
            site,
            minElevation,
            predictionHours,
            minDurationMinutes,
            cancellationToken);

        // Filter passes to only those within the session window.
        var windowPasses = allPasses
            .Where(p => p.AosUtc >= sessionStartUtc && p.LosUtc <= sessionEndUtc)
            .ToList();

        var priorities = settings.SatellitePriorities;
        var candidates = ScorePasses(windowPasses, priorities);

        var scheduled = WeightedIntervalScheduler.Solve(
            candidates,
            forcedInclusionIds: null,
            minimumElevationDeg: minElevation);

        return new SessionPlan
        {
            SessionStartUtc = sessionStartUtc,
            SessionEndUtc = sessionEndUtc,
            ScheduledPasses = scheduled,
            AllCandidates = candidates,
            ExcludedIds = new HashSet<string>(),
            ForcedInclusionIds = new HashSet<string>()
        };
    }

    /// <summary>
    /// Regenerates the plan after excluding or force-including passes.
    /// Takes the existing candidates, filters out excluded, and re-solves.
    /// </summary>
    public SessionPlan AdjustPlan(
        SessionPlan currentPlan,
        IReadOnlySet<string>? excludedIds = null,
        IReadOnlySet<string>? forcedInclusionIds = null)
    {
        var effectiveExcluded = excludedIds ?? new HashSet<string>();
        var effectiveForced = forcedInclusionIds ?? new HashSet<string>();

        // Remove excluded candidates from the candidate pool.
        var filteredCandidates = currentPlan.AllCandidates
            .Where(c => !effectiveExcluded.Contains(c.Id))
            .ToList();

        var minElevation = _settings.Current.MinimumElevationDeg;

        var scheduled = WeightedIntervalScheduler.Solve(
            filteredCandidates,
            forcedInclusionIds: effectiveForced,
            minimumElevationDeg: minElevation);

        return new SessionPlan
        {
            SessionStartUtc = currentPlan.SessionStartUtc,
            SessionEndUtc = currentPlan.SessionEndUtc,
            ScheduledPasses = scheduled,
            AllCandidates = currentPlan.AllCandidates,
            ExcludedIds = effectiveExcluded.ToHashSet(),
            ForcedInclusionIds = effectiveForced.ToHashSet()
        };
    }

    /// <summary>
    /// Rounds a DateTime up to the next 15-minute boundary.
    /// If already on a 15-minute boundary with zero seconds, returns unchanged.
    /// </summary>
    public static DateTime RoundUpTo15Minutes(DateTime input)
    {
        var totalMinutes = input.Hour * 60 + input.Minute;
        var seconds = input.Second;
        var ticks = input.Ticks % TimeSpan.TicksPerSecond;

        // If already exactly on a 15-minute boundary, return as-is.
        if (totalMinutes % 15 == 0 && seconds == 0 && ticks == 0)
            return input;

        var nextBoundaryMinutes = ((totalMinutes / 15) + 1) * 15;
        var minutesToAdd = nextBoundaryMinutes - totalMinutes;

        // Strip seconds and sub-second components, then add the minutes.
        var truncated = new DateTime(
            input.Year, input.Month, input.Day,
            input.Hour, input.Minute, 0, input.Kind);

        return truncated.AddMinutes(minutesToAdd);
    }

    /// <summary>
    /// Validates the session window. Throws ArgumentException for invalid windows.
    /// </summary>
    private static void ValidateSessionWindow(DateTime sessionStartUtc, DateTime sessionEndUtc)
    {
        if (sessionEndUtc <= sessionStartUtc)
            throw new ArgumentException(
                "Session end time must be after the start time.");

        if (sessionEndUtc - sessionStartUtc > MaxSessionDuration)
            throw new ArgumentException(
                "Session duration must not exceed 48 hours.");
    }

    /// <summary>
    /// Scores each candidate pass using elevation, duration, transponder type, and satellite priority.
    /// </summary>
    private List<ScoredPass> ScorePasses(
        IReadOnlyList<PassInfo> passes,
        Dictionary<string, int> priorities)
    {
        var scored = new List<ScoredPass>(passes.Count);

        foreach (var pass in passes)
        {
            var radioEntry = _satelliteDb.TryGetEntry(pass.SatelliteName, pass.NoradId);
            var transponderCategory = PassQualityScorer.ClassifyTransponder(radioEntry);
            var qualityScore = PassQualityScorer.ComputeScore(
                pass.MaxElevationDeg,
                pass.Duration.TotalMinutes,
                transponderCategory);

            var priority = priorities.TryGetValue(pass.SatelliteName, out var p) ? p : 5;
            var compositeScore = PassQualityScorer.ComputeCompositeScore(qualityScore, priority);

            scored.Add(new ScoredPass
            {
                Pass = pass,
                QualityScore = qualityScore,
                SatellitePriority = priority,
                CompositeScore = compositeScore
            });
        }

        return scored;
    }
}
