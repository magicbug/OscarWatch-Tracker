using OscarWatch.Core.Display;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>Sidebar pass list helpers — keep in-progress rows across refresh and match recording badges.</summary>
public static class PassSidebarMerge
{
    /// <summary>
    /// Recording may start slightly before listed AOS when elevation thresholds and the
    /// coarse pass predictor disagree by a few seconds.
    /// </summary>
    public static readonly TimeSpan RecordingEarlyStartGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Passes the sidebar and timeline should show: AOS inside the tracking prediction window,
    /// and at least the upcoming-pass minimum duration.
    /// </summary>
    public static IReadOnlyList<PassInfo> WithinPredictionWindow(
        IReadOnlyList<PassInfo> passes,
        DateTime utcNow,
        int predictionHours,
        int minimumDurationMinutes)
    {
        var end = PassUtc.Normalize(utcNow).AddHours(Math.Max(0, predictionHours));
        var minDuration = TimeSpan.FromMinutes(Math.Max(0, minimumDurationMinutes));
        return passes
            .Where(p => PassUtc.Normalize(p.AosUtc) < end && p.Duration >= minDuration)
            .ToList();
    }

    /// <summary>
    /// Re-adds passes that are still in progress when the predictor omits them (e.g. elevation dipped
    /// below the list threshold before the next 15-minute refresh).
    /// </summary>
    public static IReadOnlyList<PassInfo> MergeInProgressPasses(
        IReadOnlyList<PassInfo> predicted,
        IReadOnlyList<PassInfo> inProgressToRetain,
        DateTime utcNow)
    {
        if (inProgressToRetain.Count == 0)
            return predicted;

        var now = PassUtc.Normalize(utcNow);
        var merged = predicted.ToList();
        foreach (var pass in inProgressToRetain)
        {
            var aos = PassUtc.Normalize(pass.AosUtc);
            var los = PassUtc.Normalize(pass.LosUtc);
            if (aos > now || los <= now)
                continue;

            if (merged.Any(p => PassesOverlap(p, pass)))
                continue;

            merged.Add(pass);
        }

        return merged.Count == predicted.Count
            ? predicted
            : merged.OrderBy(p => p.AosUtc).ToList();
    }

    /// <summary>
    /// Picks the list row that owns an active recording. Never attaches to a future pass once recording
    /// has already started (except a short pre-AOS grace for the pass being recorded).
    /// </summary>
    public static PassInfo? FindPassForRecording(
        IReadOnlyList<PassInfo> passes,
        string noradId,
        DateTime utcNow,
        DateTime? recordingStartedUtc)
    {
        var now = PassUtc.Normalize(utcNow);
        var rows = passes
            .Where(p => string.Equals(p.NoradId, noradId, StringComparison.Ordinal))
            .OrderBy(p => p.AosUtc)
            .ToList();

        if (rows.Count == 0)
            return null;

        var inProgress = rows.LastOrDefault(p =>
        {
            var aos = PassUtc.Normalize(p.AosUtc);
            var los = PassUtc.Normalize(p.LosUtc);
            return now >= aos && now <= los;
        });
        if (inProgress is not null)
            return inProgress;

        if (recordingStartedUtc is { } startedRaw)
        {
            var started = PassUtc.Normalize(startedRaw);
            var atStart = rows.LastOrDefault(p =>
            {
                var aos = PassUtc.Normalize(p.AosUtc);
                var los = PassUtc.Normalize(p.LosUtc);
                return started >= aos - RecordingEarlyStartGrace && started <= los;
            });
            if (atStart is not null)
                return atStart;

            return null;
        }

        return rows.FirstOrDefault(p => now < PassUtc.Normalize(p.LosUtc));
    }

    /// <summary>
    /// Whether the sidebar row should show the REC badge while a pass recording is active.
    /// Matches the bound list row (<see cref="FindPassForRecording"/>) or any in-progress row
    /// that was active when recording started (covers AOS drift after pass-list refresh).
    /// </summary>
    public static bool IsPassRecordingTarget(
        PassInfo pass,
        string? recordingNoradId,
        DateTime? recordingPassAosUtc,
        DateTime? recordingStartedUtc,
        DateTime utcNow,
        bool isRecording)
    {
        if (!isRecording
            || string.IsNullOrEmpty(recordingNoradId)
            || !string.Equals(pass.NoradId, recordingNoradId, StringComparison.Ordinal))
            return false;

        var now = PassUtc.Normalize(utcNow);
        var aos = PassUtc.Normalize(pass.AosUtc);
        var los = PassUtc.Normalize(pass.LosUtc);

        // Still show REC in the short pre-AOS window when capture already started.
        if (now > los || now < aos - RecordingEarlyStartGrace)
            return false;

        if (recordingPassAosUtc is not null && pass.AosUtc == recordingPassAosUtc)
            return true;

        if (recordingPassAosUtc is not null
            && PassUtc.Normalize(recordingPassAosUtc.Value) == aos)
            return true;

        return recordingStartedUtc is { } startedRaw
            && PassUtc.Normalize(startedRaw) >= aos - RecordingEarlyStartGrace
            && PassUtc.Normalize(startedRaw) <= los;
    }

    private static bool PassesOverlap(PassInfo a, PassInfo b) =>
        string.Equals(a.NoradId, b.NoradId, StringComparison.Ordinal)
        && a.AosUtc <= b.LosUtc
        && b.AosUtc <= a.LosUtc;
}
