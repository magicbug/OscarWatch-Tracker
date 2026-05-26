using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Reuses expensive ground-track and footprint rings between telemetry ticks.
/// </summary>
internal sealed class SatelliteVisualCache
{
    private static readonly TimeSpan GroundTrackRefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FootprintRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Clear() => _entries.Clear();

    public void Remove(string noradId) => _entries.Remove(noradId);

    public Entry GetOrAdd(string noradId) =>
        _entries.TryGetValue(noradId, out var entry)
            ? entry
            : _entries[noradId] = new Entry();

    public bool TryGetFreshGroundTrack(string noradId, DateTime utc, out IReadOnlyList<GeoCoordinate> track)
    {
        track = [];
        if (!_entries.TryGetValue(noradId, out var entry))
            return false;

        if (utc - entry.GroundTrackUtc > GroundTrackRefreshInterval)
            return false;

        track = entry.GroundTrack;
        return track.Count >= 2;
    }

    public bool TryGetFreshFootprint(
        string noradId,
        DateTime utc,
        double minimumElevationDeg,
        out IReadOnlyList<GeoCoordinate> footprint)
    {
        footprint = [];
        if (!_entries.TryGetValue(noradId, out var entry))
            return false;

        if (Math.Abs(entry.FootprintMinElevationDeg - minimumElevationDeg) > 0.001)
            return false;

        if (utc - entry.FootprintUtc > FootprintRefreshInterval)
            return false;

        footprint = entry.Footprint;
        return footprint.Count >= 3;
    }

    internal sealed class Entry
    {
        public IReadOnlyList<GeoCoordinate> GroundTrack { get; set; } = [];
        public DateTime GroundTrackUtc;
        public IReadOnlyList<GeoCoordinate> Footprint { get; set; } = [];
        public DateTime FootprintUtc;
        public double FootprintMinElevationDeg;
        public double FootprintRadiusDeg;
    }
}
