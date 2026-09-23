using Microsoft.Data.Sqlite;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Core.Logbook;

public sealed class QsoLogbookRepository : IQsoLogbookRepository, IDisposable
{
    public event Action<long>? QsosChanged;

    private const string LogbookSelectColumns = """
        id, name, created_utc, started_utc, ended_utc, my_callsign, my_grid_square, notes,
        cloudlog_auto_upload, cloudlog_station_profile_id
        """;

    private const string QsoSelectColumns = """
        id, logbook_id, qso_utc, call, rst_sent, rst_rcvd, grid_square, name, comment,
        sat_name, mode, mode_rx, freq_hz, freq_rx_hz, band, band_rx, prop_mode, created_utc,
        cloudlog_upload_status, cloudlog_upload_attempts, cloudlog_upload_last_error, cloudlog_upload_sent_utc,
        dxcc, country
        """;

    private const string QsoSelectColumnsFromAlias = """
        q.id, q.logbook_id, q.qso_utc, q.call, q.rst_sent, q.rst_rcvd, q.grid_square, q.name, q.comment,
        q.sat_name, q.mode, q.mode_rx, q.freq_hz, q.freq_rx_hz, q.band, q.band_rx, q.prop_mode, q.created_utc,
        q.cloudlog_upload_status, q.cloudlog_upload_attempts, q.cloudlog_upload_last_error, q.cloudlog_upload_sent_utc,
        q.dxcc, q.country
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public QsoLogbookRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "qso_logbook.db");
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS logbooks (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  started_utc TEXT,
                  ended_utc TEXT,
                  my_callsign TEXT NOT NULL DEFAULT '',
                  my_grid_square TEXT NOT NULL DEFAULT '',
                  notes TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS qsos (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  logbook_id INTEGER NOT NULL,
                  qso_utc TEXT NOT NULL,
                  call TEXT NOT NULL,
                  rst_sent TEXT NOT NULL DEFAULT '',
                  rst_rcvd TEXT NOT NULL DEFAULT '',
                  grid_square TEXT NOT NULL DEFAULT '',
                  name TEXT NOT NULL DEFAULT '',
                  comment TEXT NOT NULL DEFAULT '',
                  sat_name TEXT NOT NULL DEFAULT '',
                  mode TEXT NOT NULL DEFAULT '',
                  mode_rx TEXT NOT NULL DEFAULT '',
                  freq_hz INTEGER NOT NULL DEFAULT 0,
                  freq_rx_hz INTEGER NOT NULL DEFAULT 0,
                  band TEXT NOT NULL DEFAULT '',
                  band_rx TEXT NOT NULL DEFAULT '',
                  prop_mode TEXT NOT NULL DEFAULT 'SAT',
                  created_utc TEXT NOT NULL,
                  FOREIGN KEY (logbook_id) REFERENCES logbooks(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_qsos_logbook ON qsos(logbook_id);
                CREATE INDEX IF NOT EXISTS idx_qsos_call ON qsos(call);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QsoLogbook>> ListLogbooksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {LogbookSelectColumns}
            FROM logbooks
            ORDER BY datetime(created_utc) DESC, id DESC
            """;
        return await ReadLogbooksAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QsoLogbook> CreateLogbookAsync(
        QsoLogbookCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var createdUtc = DateTime.UtcNow;
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO logbooks (name, created_utc, started_utc, ended_utc, my_callsign, my_grid_square, notes)
            VALUES ($name, $createdUtc, $startedUtc, $endedUtc, $myCallsign, $myGridSquare, $notes);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$createdUtc", FormatUtc(createdUtc));
        command.Parameters.AddWithValue("$startedUtc", (object?)FormatNullableUtc(request.StartedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$endedUtc", (object?)FormatNullableUtc(request.EndedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$myCallsign", MaidenheadLocator.NormalizeCallsign(request.MyCallsign));
        command.Parameters.AddWithValue("$myGridSquare", MaidenheadLocator.NormalizeGrids(request.MyGridSquare));
        command.Parameters.AddWithValue("$notes", request.Notes.Trim());

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return new QsoLogbook
        {
            Id = id,
            Name = request.Name.Trim(),
            CreatedUtc = createdUtc,
            StartedUtc = request.StartedUtc,
            EndedUtc = request.EndedUtc,
            MyCallsign = MaidenheadLocator.NormalizeCallsign(request.MyCallsign),
            MyGridSquare = MaidenheadLocator.NormalizeGrids(request.MyGridSquare),
            Notes = request.Notes.Trim()
        };
    }

    public async Task<QsoLogbook> UpdateLogbookAsync(
        QsoLogbookUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE logbooks
            SET name = $name, my_callsign = $myCallsign, my_grid_square = $myGridSquare
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$myCallsign", MaidenheadLocator.NormalizeCallsign(request.MyCallsign));
        command.Parameters.AddWithValue("$myGridSquare", MaidenheadLocator.NormalizeGrids(request.MyGridSquare));
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            throw new InvalidOperationException($"Logbook {request.Id} was not found.");

        await using var read = connection.CreateCommand();
        read.CommandText = $"""
            SELECT {LogbookSelectColumns}
            FROM logbooks
            WHERE id = $id
            """;
        read.Parameters.AddWithValue("$id", request.Id);
        var logbooks = await ReadLogbooksAsync(read, cancellationToken).ConfigureAwait(false);
        return logbooks.Single();
    }

    public async Task DeleteLogbookAsync(long logbookId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM logbooks WHERE id = $id";
        command.Parameters.AddWithValue("$id", logbookId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<QsoRecord>> ListQsosAsync(
        long logbookId,
        CancellationToken cancellationToken = default) =>
        ListQsosAsync(logbookId, fromUtcInclusive: null, toUtcExclusive: null, cancellationToken);

    public async Task<IReadOnlyList<QsoRecord>> ListQsosAsync(
        long logbookId,
        DateTime? fromUtcInclusive,
        DateTime? toUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE logbook_id = $logbookId
              AND ($fromUtc IS NULL OR datetime(qso_utc) >= datetime($fromUtc))
              AND ($toUtc IS NULL OR datetime(qso_utc) < datetime($toUtc))
            ORDER BY datetime(qso_utc) DESC, id DESC
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        AddNullableUtcParameter(command, "$fromUtc", fromUtcInclusive);
        AddNullableUtcParameter(command, "$toUtc", toUtcExclusive);
        return await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountQsosAsync(
        long logbookId,
        DateTime? fromUtcInclusive = null,
        DateTime? toUtcExclusive = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM qsos
            WHERE logbook_id = $logbookId
              AND ($fromUtc IS NULL OR datetime(qso_utc) >= datetime($fromUtc))
              AND ($toUtc IS NULL OR datetime(qso_utc) < datetime($toUtc))
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        AddNullableUtcParameter(command, "$fromUtc", fromUtcInclusive);
        AddNullableUtcParameter(command, "$toUtc", toUtcExclusive);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<QsoRecord>> SearchQsosByCallAsync(
        long logbookId,
        string callPrefix,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(callPrefix))
            return await ListQsosAsync(logbookId, cancellationToken).ConfigureAwait(false);

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE logbook_id = $logbookId AND call LIKE $prefix ESCAPE '\'
            ORDER BY datetime(qso_utc) DESC, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        command.Parameters.AddWithValue("$prefix", EscapeLike(callPrefix.Trim().ToUpperInvariant()) + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        return await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QsoRecord?> FindLatestQsoForCallAsync(
        long logbookId,
        string call,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(call))
            return null;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE logbook_id = $logbookId AND call = $call
            ORDER BY datetime(qso_utc) DESC, id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        command.Parameters.AddWithValue("$call", MaidenheadLocator.NormalizeCallsign(call));
        var rows = await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<QsoRecord?> FindLatestQsoForDxccAsync(
        long logbookId,
        int dxcc,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE logbook_id = $logbookId AND dxcc = $dxcc
            ORDER BY datetime(qso_utc) DESC, id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        command.Parameters.AddWithValue("$dxcc", dxcc);
        var rows = await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<QsoRecord>> ListQsosMissingDxccAsync(
        long logbookId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE logbook_id = $logbookId AND dxcc IS NULL
            ORDER BY id ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        return await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QsoRecord> AddQsoAsync(
        QsoRecordCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Call);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var createdUtc = DateTime.UtcNow;
        var qsoUtc = QsoLogbookTime.NormalizeToUtc(request.QsoUtc);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO qsos (
              logbook_id, qso_utc, call, rst_sent, rst_rcvd, grid_square, name, comment,
              sat_name, mode, mode_rx, freq_hz, freq_rx_hz, band, band_rx, prop_mode, created_utc,
              cloudlog_upload_status, dxcc, country)
            VALUES (
              $logbookId, $qsoUtc, $call, $rstSent, $rstRcvd, $gridSquare, $name, $comment,
              $satName, $mode, $modeRx, $freqHz, $freqRxHz, $band, $bandRx, $propMode, $createdUtc,
              $cloudlogUploadStatus, $dxcc, $country);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$logbookId", request.LogbookId);
        command.Parameters.AddWithValue("$qsoUtc", FormatUtc(qsoUtc));
        command.Parameters.AddWithValue("$call", MaidenheadLocator.NormalizeCallsign(request.Call));
        command.Parameters.AddWithValue("$rstSent", request.RstSent.Trim());
        command.Parameters.AddWithValue("$rstRcvd", request.RstRcvd.Trim());
        command.Parameters.AddWithValue("$gridSquare", MaidenheadLocator.NormalizeGrids(request.GridSquare));
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$comment", request.Comment.Trim());
        command.Parameters.AddWithValue("$satName", request.SatName.Trim());
        command.Parameters.AddWithValue("$mode", request.Mode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$modeRx", request.ModeRx.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$freqHz", request.FreqHz);
        command.Parameters.AddWithValue("$freqRxHz", request.FreqRxHz);
        command.Parameters.AddWithValue("$band", request.Band.Trim());
        command.Parameters.AddWithValue("$bandRx", request.BandRx.Trim());
        command.Parameters.AddWithValue("$propMode", string.IsNullOrWhiteSpace(request.PropMode) ? "SAT" : request.PropMode.Trim());
        command.Parameters.AddWithValue("$createdUtc", FormatUtc(createdUtc));
        command.Parameters.AddWithValue("$cloudlogUploadStatus", CloudlogUploadStatusCodec.ToStorage(request.CloudlogUploadStatus));
        AddNullableIntParameter(command, "$dxcc", request.Dxcc);
        command.Parameters.AddWithValue("$country", request.Country.Trim());

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        var record = new QsoRecord
        {
            Id = id,
            LogbookId = request.LogbookId,
            QsoUtc = qsoUtc,
            Call = MaidenheadLocator.NormalizeCallsign(request.Call),
            RstSent = request.RstSent.Trim(),
            RstRcvd = request.RstRcvd.Trim(),
            GridSquare = MaidenheadLocator.NormalizeGrids(request.GridSquare),
            Name = request.Name.Trim(),
            Comment = request.Comment.Trim(),
            SatName = request.SatName.Trim(),
            Mode = request.Mode.Trim().ToUpperInvariant(),
            ModeRx = request.ModeRx.Trim().ToUpperInvariant(),
            FreqHz = request.FreqHz,
            FreqRxHz = request.FreqRxHz,
            Band = request.Band.Trim(),
            BandRx = request.BandRx.Trim(),
            PropMode = string.IsNullOrWhiteSpace(request.PropMode) ? "SAT" : request.PropMode.Trim(),
            Dxcc = request.Dxcc,
            Country = request.Country.Trim(),
            CreatedUtc = createdUtc,
            CloudlogUploadStatus = request.CloudlogUploadStatus
        };

        QsosChanged?.Invoke(request.LogbookId);
        return record;
    }

    public async Task<QsoRecord> UpdateQsoAsync(
        QsoRecordUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Call);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var qsoUtc = QsoLogbookTime.NormalizeToUtc(request.QsoUtc);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE qsos SET
              qso_utc = $qsoUtc,
              call = $call,
              rst_sent = $rstSent,
              rst_rcvd = $rstRcvd,
              grid_square = $gridSquare,
              name = $name,
              comment = $comment,
              sat_name = $satName,
              mode = $mode,
              mode_rx = $modeRx,
              freq_hz = $freqHz,
              freq_rx_hz = $freqRxHz,
              band = $band,
              band_rx = $bandRx,
              prop_mode = $propMode,
              dxcc = $dxcc,
              country = $country
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$qsoUtc", FormatUtc(qsoUtc));
        command.Parameters.AddWithValue("$call", MaidenheadLocator.NormalizeCallsign(request.Call));
        command.Parameters.AddWithValue("$rstSent", request.RstSent.Trim());
        command.Parameters.AddWithValue("$rstRcvd", request.RstRcvd.Trim());
        command.Parameters.AddWithValue("$gridSquare", MaidenheadLocator.NormalizeGrids(request.GridSquare));
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$comment", request.Comment.Trim());
        command.Parameters.AddWithValue("$satName", request.SatName.Trim());
        command.Parameters.AddWithValue("$mode", request.Mode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$modeRx", request.ModeRx.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$freqHz", request.FreqHz);
        command.Parameters.AddWithValue("$freqRxHz", request.FreqRxHz);
        command.Parameters.AddWithValue("$band", request.Band.Trim());
        command.Parameters.AddWithValue("$bandRx", request.BandRx.Trim());
        command.Parameters.AddWithValue("$propMode", string.IsNullOrWhiteSpace(request.PropMode) ? "SAT" : request.PropMode.Trim());
        AddNullableIntParameter(command, "$dxcc", request.Dxcc);
        command.Parameters.AddWithValue("$country", request.Country.Trim());

        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated == 0)
            throw new InvalidOperationException($"QSO {request.Id} was not found.");

        await using var read = connection.CreateCommand();
        read.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos WHERE id = $id
            """;
        read.Parameters.AddWithValue("$id", request.Id);
        var rows = await ReadQsosAsync(read, cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    public async Task DeleteQsoAsync(long qsoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM qsos WHERE id = $id";
        command.Parameters.AddWithValue("$id", qsoId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QsoLogbook?> GetLogbookByIdAsync(long logbookId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {LogbookSelectColumns}
            FROM logbooks
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", logbookId);
        var logbooks = await ReadLogbooksAsync(command, cancellationToken).ConfigureAwait(false);
        return logbooks.FirstOrDefault();
    }

    public async Task<QsoRecord?> GetQsoByIdAsync(long qsoId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumns}
            FROM qsos
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", qsoId);
        var rows = await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<QsoLogbook> UpdateLogbookCloudlogSettingsAsync(
        QsoLogbookCloudlogSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE logbooks
            SET cloudlog_auto_upload = $autoUpload,
                cloudlog_station_profile_id = $stationProfileId
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$autoUpload", request.CloudlogAutoUpload ? 1 : 0);
        command.Parameters.AddWithValue(
            "$stationProfileId",
            request.CloudlogStationProfileId.HasValue ? request.CloudlogStationProfileId.Value : DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            throw new InvalidOperationException($"Logbook {request.Id} was not found.");

        return (await GetLogbookByIdAsync(request.Id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task UpdateQsoCloudlogUploadStateAsync(
        long qsoId,
        CloudlogUploadStatus status,
        int attempts,
        string? lastError,
        DateTime? sentUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE qsos
            SET cloudlog_upload_status = $status,
                cloudlog_upload_attempts = $attempts,
                cloudlog_upload_last_error = $lastError,
                cloudlog_upload_sent_utc = $sentUtc
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", qsoId);
        command.Parameters.AddWithValue("$status", CloudlogUploadStatusCodec.ToStorage(status));
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$lastError", lastError?.Trim() ?? "");
        command.Parameters.AddWithValue("$sentUtc", (object?)FormatNullableUtc(sentUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QsoRecord>> ListQsosPendingCloudlogUploadAsync(
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QsoSelectColumnsFromAlias}
            FROM qsos q
            INNER JOIN logbooks l ON l.id = q.logbook_id
            WHERE l.cloudlog_auto_upload = 1
              AND l.cloudlog_station_profile_id IS NOT NULL
              AND q.cloudlog_upload_status IN ('pending', 'failed')
              AND q.cloudlog_upload_attempts < 10
            ORDER BY datetime(q.qso_utc) ASC, q.id ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return await ReadQsosAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetFailedCloudlogUploadsAsync(long logbookId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE qsos
            SET cloudlog_upload_status = 'pending',
                cloudlog_upload_attempts = 0,
                cloudlog_upload_last_error = ''
            WHERE logbook_id = $logbookId
              AND cloudlog_upload_status = 'failed'
            """;
        command.Parameters.AddWithValue("$logbookId", logbookId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async Task<IReadOnlyList<QsoLogbook>> ReadLogbooksAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var list = new List<QsoLogbook>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new QsoLogbook
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                CreatedUtc = ParseUtc(reader.GetString(2)),
                StartedUtc = ParseNullableUtc(reader.IsDBNull(3) ? null : reader.GetString(3)),
                EndedUtc = ParseNullableUtc(reader.IsDBNull(4) ? null : reader.GetString(4)),
                MyCallsign = reader.GetString(5),
                MyGridSquare = reader.GetString(6),
                Notes = reader.GetString(7),
                CloudlogAutoUpload = !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
                CloudlogStationProfileId = reader.IsDBNull(9) ? null : reader.GetInt32(9)
            });
        }

        return list;
    }

    private static async Task<IReadOnlyList<QsoRecord>> ReadQsosAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var list = new List<QsoRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new QsoRecord
            {
                Id = reader.GetInt64(0),
                LogbookId = reader.GetInt64(1),
                QsoUtc = ParseUtc(reader.GetString(2)),
                Call = reader.GetString(3),
                RstSent = reader.GetString(4),
                RstRcvd = reader.GetString(5),
                GridSquare = reader.GetString(6),
                Name = reader.GetString(7),
                Comment = reader.GetString(8),
                SatName = reader.GetString(9),
                Mode = reader.GetString(10),
                ModeRx = reader.GetString(11),
                FreqHz = reader.GetInt64(12),
                FreqRxHz = reader.GetInt64(13),
                Band = reader.GetString(14),
                BandRx = reader.GetString(15),
                PropMode = reader.GetString(16),
                CreatedUtc = ParseUtc(reader.GetString(17)),
                CloudlogUploadStatus = CloudlogUploadStatusCodec.FromStorage(
                    reader.IsDBNull(18) ? null : reader.GetString(18)),
                CloudlogUploadAttempts = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                CloudlogUploadLastError = reader.IsDBNull(20) ? "" : reader.GetString(20),
                CloudlogUploadSentUtc = ParseNullableUtc(reader.IsDBNull(21) ? null : reader.GetString(21)),
                Dxcc = reader.FieldCount > 22 && !reader.IsDBNull(22) ? reader.GetInt32(22) : null,
                Country = reader.FieldCount > 23 && !reader.IsDBNull(23) ? reader.GetString(23) : ""
            });
        }

        return list;
    }

    private static string FormatUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string? FormatNullableUtc(DateTime? utc) =>
        utc is null ? null : FormatUtc(utc.Value);

    private static void AddNullableUtcParameter(SqliteCommand command, string name, DateTime? utc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = utc is null ? DBNull.Value : FormatUtc(utc.Value);
        command.Parameters.Add(parameter);
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTime? ParseNullableUtc(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseUtc(value);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void AddNullableIntParameter(SqliteCommand command, string name, int? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is null ? DBNull.Value : value.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await TryAddColumnAsync(connection, "logbooks", "cloudlog_auto_upload", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "logbooks", "cloudlog_station_profile_id", "INTEGER", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "cloudlog_upload_status", "TEXT NOT NULL DEFAULT 'none'", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "cloudlog_upload_attempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "cloudlog_upload_last_error", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "cloudlog_upload_sent_utc", "TEXT", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "dxcc", "INTEGER", cancellationToken).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "qsos", "country", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await TryCreateIndexAsync(
            connection,
            "idx_qsos_logbook_dxcc",
            "CREATE INDEX IF NOT EXISTS idx_qsos_logbook_dxcc ON qsos(logbook_id, dxcc)",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryCreateIndexAsync(
        SqliteConnection connection,
        string name,
        string ddl,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Index may already exist under a different definition; ignore.
        }
    }

    private static async Task TryAddColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
