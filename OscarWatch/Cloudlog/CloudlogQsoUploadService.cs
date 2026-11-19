using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Cloudlog;

public sealed class CloudlogQsoUploadService : ICloudlogQsoUploadService
{
    public event Action<long>? UploadStateChanged;

    private readonly IQsoLogbookRepository _repository;
    private readonly ISettingsService _settings;
    private readonly ICloudlogLookupService _lookup;
    private readonly CloudlogQsoClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _processing;

    public CloudlogQsoUploadService(
        IQsoLogbookRepository repository,
        ISettingsService settings,
        ICloudlogLookupService lookup,
        CloudlogQsoClient client)
    {
        _repository = repository;
        _settings = settings;
        _lookup = lookup;
        _client = client;
    }

    public Task QueueUploadIfEnabledAsync(long qsoId, CancellationToken cancellationToken = default)
    {
        _ = UploadInBackgroundAsync(qsoId, manual: false);
        return Task.CompletedTask;
    }

    public Task ReuploadQsoAsync(long qsoId, CancellationToken cancellationToken = default)
    {
        _ = UploadInBackgroundAsync(qsoId, manual: true);
        return Task.CompletedTask;
    }

    public Task ProcessRetryQueueAsync(CancellationToken cancellationToken = default) =>
        ProcessRetryQueueInternalAsync(cancellationToken);

    public async Task ResetFailedUploadsForRetryAsync(long logbookId, CancellationToken cancellationToken = default)
    {
        await _repository.ResetFailedCloudlogUploadsAsync(logbookId, cancellationToken).ConfigureAwait(false);
        _ = ProcessRetryQueueInternalAsync(CancellationToken.None);
    }

    public async Task<CloudlogBulkUploadResult> SendAllToCloudlogAsync(
        long logbookId,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var logbook = await _repository.GetLogbookByIdAsync(logbookId, cancellationToken).ConfigureAwait(false);
        if (logbook is null || !logbook.CloudlogStationProfileId.HasValue)
            return CloudlogBulkUploadResult.Blocked(CloudlogBulkUploadOutcome.NoStationProfile);
        if (!_lookup.CanUploadQsos(_settings.Current.Cloudlog))
            return CloudlogBulkUploadResult.Blocked(CloudlogBulkUploadOutcome.NotConfigured);

        var qsos = await _repository.ListQsosNotSentToCloudlogAsync(logbookId, cancellationToken).ConfigureAwait(false);
        var sent = 0;
        var failed = 0;
        progress?.Report((0, qsos.Count));
        for (var i = 0; i < qsos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await SendOneForBulkAsync(qsos[i].Id, cancellationToken).ConfigureAwait(false))
                sent++;
            else
                failed++;
            progress?.Report((i + 1, qsos.Count));
        }

        return new CloudlogBulkUploadResult(CloudlogBulkUploadOutcome.Completed, qsos.Count, sent, failed);
    }

    /// <summary>One QSO at a time, under the same gate as the background queue, so nothing is posted twice.</summary>
    private async Task<bool> SendOneForBulkAsync(long qsoId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _repository.GetQsoByIdAsync(qsoId, cancellationToken).ConfigureAwait(false);
            if (current?.CloudlogUploadStatus == CloudlogUploadStatus.Sent)
                return true;

            return await TryUploadQsoAsync(qsoId, cancellationToken, manual: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UploadInBackgroundAsync(long qsoId, bool manual)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (manual)
                {
                    await _repository.UpdateQsoCloudlogUploadStateAsync(
                        qsoId,
                        CloudlogUploadStatus.Pending,
                        0,
                        null,
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    NotifyUploadStateChanged(qsoId);
                }

                await TryUploadQsoAsync(qsoId, CancellationToken.None, manual).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            await ProcessRetryQueueInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Upload state is persisted in the database for later retry.
        }
    }

    private async Task ProcessRetryQueueInternalAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
            return;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var pending = await _repository.ListQsosPendingCloudlogUploadAsync(10, cancellationToken)
                        .ConfigureAwait(false);
                    if (pending.Count == 0)
                        break;

                    foreach (var qso in pending)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        await TryUploadQsoAsync(qso.Id, cancellationToken, manual: false).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    /// <summary>Returns true only when Cloudlog accepted the QSO.</summary>
    private async Task<bool> TryUploadQsoAsync(long qsoId, CancellationToken cancellationToken, bool manual)
    {
        var qso = await _repository.GetQsoByIdAsync(qsoId, cancellationToken).ConfigureAwait(false);
        if (qso is null)
            return false;

        if (!manual && qso.CloudlogUploadStatus is CloudlogUploadStatus.Sent or CloudlogUploadStatus.None)
            return false;

        var logbook = await _repository.GetLogbookByIdAsync(qso.LogbookId, cancellationToken).ConfigureAwait(false);
        if (logbook is null)
            return false;

        if (!logbook.CloudlogStationProfileId.HasValue)
        {
            if (manual)
            {
                await MarkFailedAsync(
                    qso,
                    qso.CloudlogUploadAttempts + 1,
                    "Choose a Cloudlog station profile for this logbook first.",
                    cancellationToken).ConfigureAwait(false);
                NotifyUploadStateChanged(qsoId);
            }

            return false;
        }

        if (!manual && !logbook.CloudlogAutoUpload)
            return false;

        var cloudlog = _settings.Current.Cloudlog;
        if (!_lookup.CanUploadQsos(cloudlog))
        {
            await MarkFailedAsync(
                qso,
                qso.CloudlogUploadAttempts + 1,
                "Enter your Cloudlog URL and API key in Settings → Integrations first.",
                cancellationToken).ConfigureAwait(false);
            NotifyUploadStateChanged(qsoId);
            return false;
        }

        var adif = AdifExporter.ExportRecord(logbook, qso);
        var (ok, error) = await _client.PostQsoAsync(
            cloudlog.BaseUrl,
            cloudlog.ApiKey,
            logbook.CloudlogStationProfileId.Value,
            adif,
            cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            await _repository.UpdateQsoCloudlogUploadStateAsync(
                qsoId,
                CloudlogUploadStatus.Sent,
                qso.CloudlogUploadAttempts + 1,
                null,
                DateTime.UtcNow,
                cancellationToken).ConfigureAwait(false);
            NotifyUploadStateChanged(qsoId);
            return true;
        }

        await MarkFailedAsync(
            qso,
            qso.CloudlogUploadAttempts + 1,
            error ?? "Upload failed.",
            cancellationToken).ConfigureAwait(false);
        NotifyUploadStateChanged(qsoId);
        return false;
    }

    private void NotifyUploadStateChanged(long qsoId) =>
        UploadStateChanged?.Invoke(qsoId);

    private Task MarkFailedAsync(QsoRecord qso, int attempts, string error, CancellationToken cancellationToken) =>
        _repository.UpdateQsoCloudlogUploadStateAsync(
            qso.Id,
            CloudlogUploadStatus.Failed,
            attempts,
            error,
            null,
            cancellationToken);
}
