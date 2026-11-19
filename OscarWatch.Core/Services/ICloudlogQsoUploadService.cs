namespace OscarWatch.Core.Services;

public interface ICloudlogQsoUploadService
{
    event Action<long>? UploadStateChanged;

    Task QueueUploadIfEnabledAsync(long qsoId, CancellationToken cancellationToken = default);

    /// <summary>Posts one QSO again, including one Cloudlog already accepted.</summary>
    Task ReuploadQsoAsync(long qsoId, CancellationToken cancellationToken = default);

    Task ProcessRetryQueueAsync(CancellationToken cancellationToken = default);

    Task ResetFailedUploadsForRetryAsync(long logbookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts every QSO in the logbook that is not yet sent, oldest first, using the saved station location.
    /// Reports (done, total) as it goes.
    /// </summary>
    Task<CloudlogBulkUploadResult> SendAllToCloudlogAsync(
        long logbookId,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum CloudlogBulkUploadOutcome
{
    Completed,
    NotConfigured,
    NoStationProfile
}

/// <param name="Outcome">Whether the run started. Blocked runs have zero counts.</param>
/// <param name="Total">QSOs that needed sending when the run started.</param>
/// <param name="Sent">QSOs now in Cloudlog, including any the background queue sent first.</param>
/// <param name="Failed">QSOs Cloudlog did not accept.</param>
public sealed record CloudlogBulkUploadResult(CloudlogBulkUploadOutcome Outcome, int Total, int Sent, int Failed)
{
    public static CloudlogBulkUploadResult Blocked(CloudlogBulkUploadOutcome outcome) => new(outcome, 0, 0, 0);
}
