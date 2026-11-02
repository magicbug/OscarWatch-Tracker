using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Background propagation of enabled satellites; UI reads the latest snapshot only.
/// </summary>
public interface ILiveTrackingService : IDisposable
{
    /// <summary>UTC time of the last completed snapshot (or <see cref="DateTime.MinValue"/> if none yet).</summary>
    DateTime SnapshotUtc { get; }

    /// <summary>Offset applied to <see cref="DateTime.UtcNow"/> for map/sky propagation. Zero is live.</summary>
    TimeSpan MapTimeOffset { get; set; }

    /// <summary>Map-focused NORAD id; ground track geometry is built only for this satellite.</summary>
    string? FocusedNoradId { get; set; }

    /// <summary>Latest propagated states at map display time (UTC + <see cref="MapTimeOffset"/>). Do not mutate.</summary>
    IReadOnlyList<SatelliteTrackState> GetSnapshot();

    /// <summary>
    /// The latest states and the time they were propagated for, read together. Reading
    /// <see cref="GetSnapshot"/> and <see cref="SnapshotUtc"/> separately can pair the states of one
    /// tick with the time of the next. The default reads them one after the other; the real service
    /// overrides it to read both under one lock.
    /// </summary>
    (IReadOnlyList<SatelliteTrackState> States, DateTime Utc) GetSnapshotWithUtc() => (GetSnapshot(), SnapshotUtc);

    /// <summary>UTC time of the last completed live-now snapshot.</summary>
    DateTime LiveNowSnapshotUtc { get; }

    /// <summary>
    /// Latest propagated states at real tracking time (no map offset).
    /// When <see cref="MapTimeOffset"/> is zero, returns the same list as <see cref="GetSnapshot"/>.
    /// </summary>
    IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot();

    /// <summary>Starts the background worker (idempotent).</summary>
    void Start();

    /// <summary>Reloads enabled satellites in the propagator and refreshes the snapshot on the worker.</summary>
    void RequestReload();
}
