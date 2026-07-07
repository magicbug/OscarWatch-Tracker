using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public static class PassConflictDetector
{
    public static PassConflictResult Detect(IReadOnlyList<PassInfo>? passes, TimeSpan minimumOverlap)
    {
        if (passes is null || passes.Count < 2)
            return PassConflictResult.Empty;

        // Create sorted events
        var events = new List<(DateTime Time, bool IsStart, int Index)>(passes.Count * 2);
        for (var i = 0; i < passes.Count; i++)
        {
            events.Add((passes[i].AosUtc, true, i));
            events.Add((passes[i].LosUtc, false, i));
        }

        events.Sort((a, b) =>
        {
            var cmp = a.Time.CompareTo(b.Time);
            if (cmp != 0) return cmp;
            // LOS before AOS at same time (end before start)
            return a.IsStart.CompareTo(b.IsStart);
        });

        // Sweep
        var active = new HashSet<int>();
        var conflicts = new List<PassConflict>();

        foreach (var (time, isStart, index) in events)
        {
            if (isStart)
            {
                foreach (var activeIdx in active)
                {
                    var passA = passes[activeIdx];
                    var passB = passes[index];

                    // Skip same-satellite
                    if (string.Equals(passA.NoradId, passB.NoradId, StringComparison.Ordinal))
                        continue;

                    // Compute overlap
                    var overlapStart = passA.AosUtc > passB.AosUtc ? passA.AosUtc : passB.AosUtc;
                    var overlapEnd = passA.LosUtc < passB.LosUtc ? passA.LosUtc : passB.LosUtc;
                    var overlap = overlapEnd - overlapStart;

                    if (overlap >= minimumOverlap)
                        conflicts.Add(new PassConflict(passA, passB, overlap));
                }

                active.Add(index);
            }
            else
            {
                active.Remove(index);
            }
        }

        return new PassConflictResult(conflicts);
    }
}
