using System.Text.Json;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Core.Sstv;

/// <summary>A received picture waiting to be shared to the OscarWatch.org SSTV gallery.</summary>
public sealed record SstvShareItem(
    string PicturePath,
    string Satellite,
    string? NoradId,
    string Mode,
    DateTime ReceivedUtc,
    double SyncLock,
    bool DeleteAfterShare);

public static class SstvShareRules
{
    /// <summary>The gallery turns away pictures below this sync lock.</summary>
    public const double MinSyncLock = 50;

    /// <summary>The share option is only offered once an OscarWatch API token is saved.</summary>
    public static bool CanShare(SatelliteStatusSettings connection) =>
        Ft4OscarWatchSpots.HasApiToken(connection.ApiToken);

    /// <summary>
    /// True when a finished picture from a live session should be queued: sharing is on with an
    /// API token, the satellite is known, the picture ran to the end, and the lock is at least 50%.
    /// Recordings decoded from a file are never shared.
    /// </summary>
    public static bool ShouldQueue(
        bool sharingEnabled,
        string? apiToken,
        bool fromRecording,
        string? satellite,
        SstvDecodedImage image) =>
        sharingEnabled
        && Ft4OscarWatchSpots.HasApiToken(apiToken)
        && IsShareable(fromRecording, satellite, image);

    /// <summary>The picture itself qualifies: live, satellite known, finished, lock at least 50%.</summary>
    public static bool IsShareable(bool fromRecording, string? satellite, SstvDecodedImage image) =>
        !fromRecording
        && !string.IsNullOrWhiteSpace(satellite)
        && image.Complete
        && double.IsFinite(image.LockPercent)
        && image.LockPercent >= MinSyncLock;
}

/// <summary>
/// Works out when a satellite has set, so pictures from that pass can be shared. A satellite
/// has set after <see cref="ConfirmSamples"/> samples in a row below the horizon with the
/// elevation still falling (a satellite below the horizon but rising has not had its pass yet).
/// </summary>
public sealed class SstvPassEndWatcher
{
    public const int ConfirmSamples = 3;
    public const double HorizonDeg = 0.0;

    /// <summary>When the elevation of a satellite is unknown, share this long after the picture.</summary>
    public static readonly TimeSpan UnknownElevationDelay = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    public void Sample(string noradId, double? elevationDeg, DateTime nowUtc)
    {
        if (!_tracks.TryGetValue(noradId, out var track))
        {
            track = new Track();
            _tracks[noradId] = track;
        }

        if (elevationDeg is not { } el || !double.IsFinite(el))
        {
            track.Known = false;
            track.Below = 0;
            track.LastElevation = null;
            return;
        }

        track.Known = true;
        var falling = track.LastElevation is { } last && el < last;
        track.Below = el < HorizonDeg && falling ? track.Below + 1 : 0;
        track.LastElevation = el;
        if (track.Below >= ConfirmSamples)
            track.LastSetUtc = nowUtc;
    }

    /// <summary>True once the satellite the picture came from has set after the picture was received.</summary>
    public bool IsPassOver(SstvShareItem item, DateTime nowUtc)
    {
        if (item.NoradId is { Length: > 0 } norad && _tracks.TryGetValue(norad, out var track) && track.Known)
            return track.LastSetUtc is { } set && set > item.ReceivedUtc;
        return nowUtc - item.ReceivedUtc >= UnknownElevationDelay;
    }

    public void Forget(string noradId) => _tracks.Remove(noradId);

    private sealed class Track
    {
        public bool Known;
        public int Below;
        public double? LastElevation;
        public DateTime? LastSetUtc;
    }
}

public enum SstvShareState
{
    Idle,
    WaitingForPass,
    Uploading,
    Shared,
    Rejected,
    Stopped,
    RateLimited,
    Retrying
}

/// <summary>Sharing progress for the SSTV window. <see cref="Detail"/> is the server message or picture status.</summary>
public sealed record SstvShareStatus(SstvShareState State, int Pending, string? Detail = null);

/// <summary>Reads and writes the share queue as JSON next to the pictures.</summary>
public static class SstvShareQueueStore
{
    public const string FileName = "share-queue.json";
    public const string PictureFolderName = "share-queue";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultQueuePath => Path.Combine(SstvPaths.GetDefaultDirectory(), FileName);

    /// <summary>Holds pictures kept only to be shared, when automatic saving is off.</summary>
    public static string DefaultPictureFolder => Path.Combine(SstvPaths.GetDefaultDirectory(), PictureFolderName);

    public static List<SstvShareItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var items = JsonSerializer.Deserialize<List<SstvShareItem>>(File.ReadAllText(path), JsonOptions);
            return items?.Where(i => !string.IsNullOrWhiteSpace(i.PicturePath) && !string.IsNullOrWhiteSpace(i.Satellite)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Save(string path, IReadOnlyList<SstvShareItem> items)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (items.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Shares received SSTV pictures to the OscarWatch.org gallery after the pass. Pictures are
/// held until the satellite they came from has set, so uploads never compete with decoding,
/// then sent one at a time in the background. The queue survives a restart.
/// </summary>
public sealed class SstvPictureUploader : IDisposable
{
    public const int MaxQueued = 200;
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(15);

    private readonly Func<SatelliteStatusSettings, SstvPictureUpload, CancellationToken, Task<SstvPictureShareResult>> _upload;
    private readonly Func<SatelliteStatusSettings> _connection;
    private readonly Func<bool> _enabled;
    private readonly Func<string, double?> _elevation;
    private readonly Func<DateTime> _clock;
    private readonly string? _queuePath;
    private readonly string _client;
    private readonly List<SstvShareItem> _items;
    private readonly SstvPassEndWatcher _watcher = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private DateTime _holdUntilUtc = DateTime.MinValue;
    private int _retryCount;
    private string? _stoppedFor;
    private SstvShareStatus _status;
    private int _disposed;

    public SstvPictureUploader(
        ISstvPictureShareService service,
        Func<SatelliteStatusSettings> connection,
        Func<bool> enabled,
        Func<string, double?> elevation,
        string client)
        : this(
            (settings, picture, ct) => service.UploadAsync(settings, picture, ct),
            connection,
            enabled,
            elevation,
            client,
            SstvShareQueueStore.DefaultQueuePath)
    {
    }

    public SstvPictureUploader(
        Func<SatelliteStatusSettings, SstvPictureUpload, CancellationToken, Task<SstvPictureShareResult>> upload,
        Func<SatelliteStatusSettings> connection,
        Func<bool> enabled,
        Func<string, double?> elevation,
        string client,
        string? queuePath,
        Func<DateTime>? clock = null)
    {
        _upload = upload;
        _connection = connection;
        _enabled = enabled;
        _elevation = elevation;
        _client = client;
        _queuePath = queuePath;
        _clock = clock ?? (() => DateTime.UtcNow);
        _items = queuePath is null ? [] : SstvShareQueueStore.Load(queuePath);
        _status = new SstvShareStatus(_items.Count > 0 ? SstvShareState.WaitingForPass : SstvShareState.Idle, _items.Count);
    }

    /// <summary>Raised on a background thread when the sharing state or queue length changes.</summary>
    public event EventHandler<SstvShareStatus>? StatusChanged;

    /// <summary>Operator-facing diagnostics (message, optional exception) for the host application to log.</summary>
    public event Action<string, Exception?>? Diagnostic;

    public SstvShareStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public int PendingCount
    {
        get { lock (_gate) return _items.Count; }
    }

    public IReadOnlyList<SstvShareItem> Pending
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    /// <summary>Sharing is switched on and an API token is set.</summary>
    public bool IsActive => _enabled() && Ft4OscarWatchSpots.HasApiToken(_connection().ApiToken);

    /// <summary>Starts the background loop that checks the queue every <paramref name="interval"/>.</summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_worker is not null || Volatile.Read(ref _disposed) != 0)
            return;
        var every = interval ?? DefaultTickInterval;
        _worker = Task.Run(() => RunAsync(every, _cts.Token));
    }

    public void Enqueue(SstvShareItem item)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        SstvShareItem? dropped = null;
        lock (_gate)
        {
            _items.Add(item);
            if (_items.Count > MaxQueued)
            {
                dropped = _items[0];
                _items.RemoveAt(0);
            }

            PersistLocked();
        }

        if (dropped is not null)
            DeletePictureIfTemporary(dropped);
        Diagnostic?.Invoke($"SSTV picture queued to share after the {item.Satellite} pass: {item.PicturePath}", null);
        Publish(SstvShareState.WaitingForPass);
    }

    /// <summary>Drops every queued picture (sharing was switched off).</summary>
    public void Clear()
    {
        SstvShareItem[] removed;
        lock (_gate)
        {
            removed = _items.ToArray();
            _items.Clear();
            _stoppedFor = null;
            _holdUntilUtc = DateTime.MinValue;
            _retryCount = 0;
            PersistLocked();
        }

        foreach (var item in removed)
            DeletePictureIfTemporary(item);
        Publish(SstvShareState.Idle);
    }

    /// <summary>One pass over the queue: sample elevations, then share at most one picture whose pass is over.</summary>
    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _processing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            await ProcessCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processing.Release();
        }
    }

    private async Task ProcessCoreAsync(CancellationToken cancellationToken)
    {
        SstvShareItem[] items;
        lock (_gate)
            items = _items.ToArray();
        if (items.Length == 0)
            return;

        var now = _clock();
        foreach (var norad in items.Select(i => i.NoradId).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal))
            _watcher.Sample(norad!, SafeElevation(norad!), now);

        if (!_enabled())
            return;

        var settings = _connection();
        if (!Ft4OscarWatchSpots.HasApiToken(settings.ApiToken))
            return;

        var key = ConnectionKey(settings);
        lock (_gate)
        {
            if (_stoppedFor is not null)
            {
                if (string.Equals(_stoppedFor, key, StringComparison.Ordinal))
                    return;
                _stoppedFor = null;
            }

            if (now < _holdUntilUtc)
                return;
        }

        var ready = items
            .Where(i => _watcher.IsPassOver(i, now))
            .OrderBy(i => i.ReceivedUtc)
            .FirstOrDefault();
        if (ready is null)
        {
            if (Status.State is SstvShareState.Idle or SstvShareState.Uploading or SstvShareState.RateLimited or SstvShareState.Retrying)
                Publish(SstvShareState.WaitingForPass);
            return;
        }

        if (!File.Exists(ready.PicturePath))
        {
            Remove(ready);
            Diagnostic?.Invoke($"SSTV picture to share is missing and was dropped: {ready.PicturePath}", null);
            return;
        }

        Publish(SstvShareState.Uploading, ready.Satellite);
        SstvPictureShareResult result;
        try
        {
            result = await _upload(settings, ToUpload(ready), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = new SstvPictureShareResult(false, ex.Message, 0);
        }

        Handle(ready, result, key, now);
    }

    private void Handle(SstvShareItem item, SstvPictureShareResult result, string key, DateTime now)
    {
        if (result.Accepted)
        {
            lock (_gate)
                _retryCount = 0;
            Remove(item);
            Diagnostic?.Invoke($"SSTV picture shared to OscarWatch.org ({result.Status ?? "stored"}): {item.PicturePath}", null);
            Publish(SstvShareState.Shared, result.Status);
            return;
        }

        switch (result.HttpStatusCode)
        {
            case 422:
                Remove(item);
                Diagnostic?.Invoke($"SSTV picture not accepted by OscarWatch.org and dropped: {result.Message} ({item.PicturePath})", null);
                Publish(SstvShareState.Rejected, result.Message);
                break;
            case 401 or 403 or 404:
                lock (_gate)
                    _stoppedFor = key;
                Diagnostic?.Invoke($"SSTV picture sharing stopped: {result.Message}", null);
                Publish(SstvShareState.Stopped, result.Message);
                break;
            case 429:
                lock (_gate)
                    _holdUntilUtc = now + (result.RetryAfter ?? DefaultRateLimitWait);
                Diagnostic?.Invoke($"SSTV picture sharing is rate limited: {result.Message}", null);
                Publish(SstvShareState.RateLimited, result.Message);
                break;
            default:
                TimeSpan wait;
                lock (_gate)
                {
                    _retryCount++;
                    var seconds = Math.Min(MaxRetryWait.TotalSeconds, 30 * Math.Pow(2, _retryCount - 1));
                    wait = TimeSpan.FromSeconds(seconds);
                    _holdUntilUtc = now + wait;
                }

                Diagnostic?.Invoke($"SSTV picture share failed, retrying in {wait.TotalSeconds:0} s: {result.Message}", null);
                Publish(SstvShareState.Retrying, result.Message);
                break;
        }
    }

    private SstvPictureUpload ToUpload(SstvShareItem item) =>
        new(item.PicturePath, item.Satellite, item.Mode, item.ReceivedUtc, item.SyncLock, _client, item.NoradId);

    private double? SafeElevation(string noradId)
    {
        try
        {
            return _elevation(noradId);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"SSTV share could not read the elevation of {noradId}.", ex);
            return null;
        }
    }

    private void Remove(SstvShareItem item)
    {
        lock (_gate)
        {
            _items.Remove(item);
            PersistLocked();
        }

        DeletePictureIfTemporary(item);
    }

    private void PersistLocked()
    {
        if (_queuePath is null)
            return;
        try
        {
            SstvShareQueueStore.Save(_queuePath, _items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostic?.Invoke("SSTV share queue could not be saved.", ex);
        }
    }

    private void DeletePictureIfTemporary(SstvShareItem item)
    {
        if (!item.DeleteAfterShare)
            return;
        try
        {
            if (File.Exists(item.PicturePath))
                File.Delete(item.PicturePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostic?.Invoke($"Could not delete the shared SSTV picture {item.PicturePath}.", ex);
        }
    }

    private void Publish(SstvShareState state, string? detail = null)
    {
        SstvShareStatus status;
        lock (_gate)
        {
            status = new SstvShareStatus(state, _items.Count, detail);
            if (status == _status)
                return;
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ProcessOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke("SSTV share queue check failed.", ex);
                }

                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string ConnectionKey(SatelliteStatusSettings settings) =>
        $"{settings.BaseUrl?.Trim()}|{settings.ApiToken?.Trim()}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}
