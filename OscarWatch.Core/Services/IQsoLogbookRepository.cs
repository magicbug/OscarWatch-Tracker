using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public interface IQsoLogbookRepository
{
    /// <summary>Raised after a QSO is inserted. The argument is the logbook id.</summary>
    event Action<long>? QsosChanged;

    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QsoLogbook>> ListLogbooksAsync(CancellationToken cancellationToken = default);

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
}
