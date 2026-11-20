using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public interface IQsoLogbookRepository
{
    /// <summary>Raised after a QSO is inserted. The argument is the logbook id.</summary>
    event Action<long>? QsosChanged;

    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoLogbook>> ListLogbooksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest logbook, or creates one from <paramref name="request"/> when none exist.
    /// Concurrent callers share a single create.
    /// </summary>
    Task<QsoLogbook> GetOrCreateLogbookAsync(QsoLogbookCreateRequest request, CancellationToken cancellationToken = default);

    Task<QsoLogbook> CreateLogbookAsync(QsoLogbookCreateRequest request, CancellationToken cancellationToken = default);

    Task<QsoLogbook> UpdateLogbookAsync(QsoLogbookUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteLogbookAsync(long logbookId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoRecord>> ListQsosAsync(
        long logbookId,
        DateTime? fromUtcInclusive = null,
        DateTime? toUtcExclusive = null,
        CancellationToken cancellationToken = default);

    Task<int> CountQsosAsync(
        long logbookId,
        DateTime? fromUtcInclusive = null,
        DateTime? toUtcExclusive = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoRecord>> SearchQsosByCallAsync(
        long logbookId,
        string callPrefix,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<QsoRecord?> FindLatestQsoForCallAsync(
        long logbookId,
        string call,
        CancellationToken cancellationToken = default);

    Task<QsoRecord?> FindLatestQsoForDxccAsync(
        long logbookId,
        int dxcc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoRecord>> ListQsosMissingDxccAsync(
        long logbookId,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<QsoRecord> AddQsoAsync(QsoRecordCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts many QSOs in one transaction and raises <see cref="QsosChanged"/> once.
    /// When <paramref name="preserveBlankPropMode"/> is true, an empty propagation mode stays empty
    /// (used by ADIF import). Otherwise blank becomes <c>SAT</c>, matching a manual add.
    /// </summary>
    Task<IReadOnlyList<QsoRecord>> AddQsosBatchAsync(
        IReadOnlyList<QsoRecordCreateRequest> requests,
        bool preserveBlankPropMode = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Duplicate keys for ADIF import: normalised callsign plus UTC date and time to the minute.
    /// </summary>
    Task<IReadOnlySet<string>> ListQsoDuplicateKeysAsync(
        long logbookId,
        CancellationToken cancellationToken = default);

    Task<QsoRecord> UpdateQsoAsync(QsoRecordUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteQsoAsync(long qsoId, CancellationToken cancellationToken = default);

    Task<QsoLogbook?> GetLogbookByIdAsync(long logbookId, CancellationToken cancellationToken = default);

    Task<QsoRecord?> GetQsoByIdAsync(long qsoId, CancellationToken cancellationToken = default);

    Task<QsoLogbook> UpdateLogbookCloudlogSettingsAsync(
        QsoLogbookCloudlogSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task UpdateQsoCloudlogUploadStateAsync(
        long qsoId,
        CloudlogUploadStatus status,
        int attempts,
        string? lastError,
        DateTime? sentUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoRecord>> ListQsosPendingCloudlogUploadAsync(
        int limit = 25,
        CancellationToken cancellationToken = default);

    Task ResetFailedCloudlogUploadsAsync(long logbookId, CancellationToken cancellationToken = default);

    /// <summary>QSOs in one logbook that are not yet sent to Cloudlog (none, pending or failed), oldest first.</summary>
    Task<IReadOnlyList<QsoRecord>> ListQsosNotSentToCloudlogAsync(
        long logbookId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct worked callsigns and 4-character Maidenhead fields from every logbook,
    /// for FT4 decode shading of new stations and new grids.
    /// </summary>
    Task<(IReadOnlySet<string> Calls, IReadOnlySet<string> GridFields)> LoadWorkedCallAndGridFieldsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Worked callsigns FT4 sends as a hash (prefixed, a suffix other than /P or /R,
    /// or longer than six characters), most recent first.
    /// FT4 sends these as a hash, so the decoder needs them before it can name the station.
    /// </summary>
    Task<IReadOnlyList<string>> ListRecentNonStandardCallsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
