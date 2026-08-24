using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Core.Services;

public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Keep this many timestamped backups besides <c>settings.json.bak</c>.</summary>
    internal const int MaxTimestampedBackups = 10;
    private const int MinimumPersistedJsonLength = 32;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Timer? _saveTimer;
    private volatile bool _savePending;
    private bool _canPersist = true;
    private bool _allowFactoryOverwrite;
    private string? _loadError;
    private const int SaveQuietPeriodMs = 500;

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "settings.json");

        _saveTimer = new Timer(OnSaveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current { get; private set; } = new();

    public string SettingsPath { get; }

    public string? LoadError => _loadError;

    public bool CanPersist => _canPersist;

    /// <summary>
    /// Indicates whether a save is pending (exposed for testing).
    /// </summary>
    internal bool SavePending => _savePending;

    public static event Action<Exception>? SaveFailed;

    public string SerializeCurrent() =>
        JsonSerializer.Serialize(Current, JsonOptions);

    public static bool TryParse(string json, out AppSettings settings, out string? error)
    {
        settings = new AppSettings();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "File is empty.";
            return false;
        }

        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            MigrateDisplayTimesInUtc(json, settings);
            MigrateMissingEnabledSatelliteNoradIds(json, settings);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        NormalizeSettings(settings);
        return true;
    }

    public async Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default)
    {
        NormalizeSettings(imported);
        Current = imported;
        EnsureSavedStations();

        if (string.IsNullOrWhiteSpace(Current.GroundStation.GridSquare))
            SyncGridFromLatLon();

        // Explicit operator import may repair a previously blocked load.
        _loadError = null;
        _canPersist = true;
        _allowFactoryOverwrite = true;
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _allowFactoryOverwrite = false;
        }
    }

    public void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _loadError = null;
        _canPersist = true;

        if (!File.Exists(SettingsPath))
        {
            Current = new AppSettings();
            SyncGridFromLatLon();
            EnsureSavedStations();
            SaveToDisk();
            return;
        }

        var json = File.ReadAllText(SettingsPath);
        if (!TryParse(json, out var settings, out var parseError))
        {
            // Keep the on-disk file untouched. Session falls back to defaults but must not persist.
            TryCopyCorruptSnapshot(SettingsPath, json);
            Current = new AppSettings();
            EnsureSavedStations();
            if (string.IsNullOrWhiteSpace(Current.GroundStation.GridSquare))
                SyncGridFromLatLon();

            _canPersist = false;
            _loadError = string.IsNullOrWhiteSpace(parseError)
                ? "Settings file could not be read. On-disk settings were left unchanged."
                : $"Settings file could not be read ({parseError}). On-disk settings were left unchanged.";
            Trace.TraceError("OscarWatch settings load failed; refusing to overwrite {0}: {1}", SettingsPath, _loadError);
            return;
        }

        Current = settings;
        EnsureSavedStations();

        if (string.IsNullOrWhiteSpace(Current.GroundStation.GridSquare))
            SyncGridFromLatLon();

        // If the live file was overwritten with factory defaults but .bak still has a real QTH, recover it.
        if (TryRecoverPersonalizedSettingsFromBackup(ref settings))
        {
            Current = settings;
            EnsureSavedStations();
            if (string.IsNullOrWhiteSpace(Current.GroundStation.GridSquare))
                SyncGridFromLatLon();
            try
            {
                // Repair settings.json immediately so the next launch is clean.
                _allowFactoryOverwrite = true;
                SaveToDisk();
            }
            finally
            {
                _allowFactoryOverwrite = false;
            }

            Trace.TraceWarning(
                "OscarWatch restored personalized settings from backup after settings.json looked like factory defaults.");
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Load(), cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(SaveToDisk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportSaveFailed(ex);
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void RequestSave()
    {
        if (!_canPersist)
        {
            Trace.TraceWarning(
                "OscarWatch settings RequestSave ignored; load failed and on-disk settings must not be overwritten.");
            return;
        }

        _savePending = true;
        _saveTimer?.Change(SaveQuietPeriodMs, Timeout.Infinite);
    }

    private void OnSaveTimerElapsed(object? state)
    {
        if (!_savePending) return;
        _savePending = false;
        _ = SaveAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _savePending = true; // Retry on next trigger
                _ = t.Exception; // Observe the exception (already reported by SaveAsync)
            }
        }, TaskScheduler.Default);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (!_canPersist)
        {
            _savePending = false;
            return;
        }

        if (_savePending)
        {
            _savePending = false;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Stop the timer to prevent further callbacks
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _saveTimer?.Dispose();
        _saveTimer = null;
        _saveGate.Dispose();
    }

    public void EnsureSavedStations()
    {
        if (Current.SavedStations.Count == 0)
        {
            var home = StationProfile.FromGroundStation(Current.GroundStation);
            Current.SavedStations.Add(home);
            Current.ActiveStationId = home.Id;
        }

        if (string.IsNullOrWhiteSpace(Current.ActiveStationId))
            Current.ActiveStationId = Current.SavedStations[0].Id;

        ApplyActiveStation();
    }

    public void ApplyActiveStation()
    {
        var profile = Current.SavedStations.FirstOrDefault(s => s.Id == Current.ActiveStationId)
            ?? Current.SavedStations.FirstOrDefault();
        if (profile is null)
            return;

        Current.ActiveStationId = profile.Id;
        Current.GroundStation = profile.ToGroundStation();
    }

    public void SyncActiveStationFromGroundStation()
    {
        var profile = Current.SavedStations.FirstOrDefault(s => s.Id == Current.ActiveStationId);
        if (profile is null)
            return;

        profile.DisplayName = Current.GroundStation.DisplayName;
        profile.LatitudeDeg = Current.GroundStation.LatitudeDeg;
        profile.LongitudeDeg = Current.GroundStation.LongitudeDeg;
        profile.AltitudeMetersAsl = Current.GroundStation.AltitudeMetersAsl;
        profile.GridSquare = Current.GroundStation.GridSquare;
        profile.HorizonMask = Current.GroundStation.HorizonMask?.Clone() ?? new HorizonMask();
    }

    public void SyncGridFromLatLon()
    {
        Current.GroundStation.GridSquare = MaidenheadGrid.FromLatLon(
            Current.GroundStation.LatitudeDeg,
            Current.GroundStation.LongitudeDeg);
    }

    public void SyncLatLonFromGrid()
    {
        var (lat, lon) = MaidenheadGrid.ToLatLonCenter(Current.GroundStation.GridSquare);
        Current.GroundStation.LatitudeDeg = lat;
        Current.GroundStation.LongitudeDeg = lon;
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.GroundStation ??= new GroundStation();
        settings.VoiceAnnouncements ??= new VoiceAnnouncementSettings();
        settings.PassSchedule ??= new PassScheduleSettings();
        settings.PassSchedule.LeadMinutesBeforeAos =
            PassScheduleSettings.ClampLeadMinutes(settings.PassSchedule.LeadMinutesBeforeAos);
        settings.ScheduledPasses ??= [];
        settings.FrequencySelections ??= new Dictionary<string, SatelliteFrequencySelection>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in settings.FrequencySelections.Values)
        {
            selection.ModeOffsets ??= new Dictionary<string, ModeOffsetSettings>(StringComparer.OrdinalIgnoreCase);
            selection.CwUplinkByMode ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            selection.CwReceiveOffsetKHzByMode ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            selection.DopplerStrategyByMode ??= new Dictionary<string, DopplerStrategy>(StringComparer.OrdinalIgnoreCase);
        }

        settings.Rotator ??= new RotatorSettings();
        settings.Gps ??= new GpsSettings();
        settings.Rig ??= new RigSettings();
        settings.Rig.MigrateFt817818ToDualOnly();
        if (settings.Rig.DopplerCatLeadGainPercent is <= 0 or > 100)
            settings.Rig.DopplerCatLeadGainPercent = RigSettings.DefaultDopplerCatLeadGainPercent;
        settings.Rig.DopplerCatLeadMs = Math.Clamp(settings.Rig.DopplerCatLeadMs, 0, DopplerCatLead.UserLeadMsMax);
        settings.Cloudlog ??= new CloudlogSettings();
        settings.SatelliteLink ??= new SatelliteLinkSettings();
        settings.SatelliteLink.Port = SatelliteLinkSettings.NormalizePort(settings.SatelliteLink.Port);
        settings.SatelliteLink.UpdateIntervalMs =
            SatelliteLinkSettings.NormalizeUpdateIntervalMs(settings.SatelliteLink.UpdateIntervalMs);
        settings.PassRecording ??= new PassRecordingSettings();
        settings.PassRecording.MigrateLegacyNumericDeviceId();
        settings.QsoLogbook ??= new QsoLogbookSettings();
        settings.QsoLogbook.HistoryColumnWidthsPx ??=
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        settings.TleSource ??= new TleSourceSettings();
        settings.TleSource = TleSourceResolver.NormalizeLegacyCustomUrl(settings.TleSource);
        settings.TransponderConflictAcknowledgments ??= [];
        settings.EnabledSatelliteNames ??= [];
        settings.EnabledSatelliteNoradIds ??= [];
    }

    /// <summary>
    /// Before <see cref="AppSettings.EnabledSatelliteNoradIds"/> existed, enablement was name-only.
    /// Missing JSON must not inherit the new-install default ID list.
    /// </summary>
    private static void MigrateMissingEnabledSatelliteNoradIds(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("enabledSatelliteNoradIds", out _))
                return;

            settings.EnabledSatelliteNoradIds = [];
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// Before <see cref="AppSettings.DisplayTimesInUtc"/> existed, <c>passPlannerUseUtcTime</c> drove all UI times.
    /// </summary>
    private static void MigrateDisplayTimesInUtc(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("displayTimesInUtc", out _))
                return;

            if (root.TryGetProperty("passPlannerUseUtcTime", out var legacy) && legacy.ValueKind == JsonValueKind.True)
                settings.DisplayTimesInUtc = true;
        }
        catch (JsonException)
        {
        }
    }

    private void SaveToDisk()
    {
        if (!_canPersist)
        {
            throw new InvalidOperationException(
                _loadError
                ?? "Refusing to overwrite settings.json after a failed load. Fix or restore the file, or import settings explicitly.");
        }

        var json = JsonSerializer.Serialize(Current, JsonOptions);
        if (string.IsNullOrWhiteSpace(json) || json.Length < MinimumPersistedJsonLength)
        {
            throw new InvalidOperationException(
                "Refusing to write an empty or truncated settings payload.");
        }

        if (!_allowFactoryOverwrite
            && File.Exists(SettingsPath)
            && LooksLikeFactoryGroundStation(Current.GroundStation)
            && OnDiskLooksPersonalized(SettingsPath))
        {
            throw new InvalidOperationException(
                "Refusing to overwrite personalized on-disk settings with factory-default station data.");
        }

        WriteAtomic(SettingsPath, json);
    }

    internal static bool LooksLikeFactoryGroundStation(GroundStation station)
    {
        if (station is null)
            return true;

        return Math.Abs(station.LatitudeDeg - 51.5) < 0.000_001
            && Math.Abs(station.LongitudeDeg - (-0.1)) < 0.000_001
            && string.Equals(station.GridSquare, "IO91wm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool OnDiskLooksPersonalized(string settingsPath)
    {
        try
        {
            if (!TryParse(File.ReadAllText(settingsPath), out var disk, out _))
                return false;

            return !LooksLikeFactoryGroundStation(disk.GroundStation);
        }
        catch
        {
            return false;
        }
    }

    private bool TryRecoverPersonalizedSettingsFromBackup(ref AppSettings loaded)
    {
        if (!LooksLikeFactoryGroundStation(loaded.GroundStation))
            return false;

        foreach (var candidate in EnumerateRecoveryCandidates())
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;

                if (!TryParse(File.ReadAllText(candidate), out var recovered, out _))
                    continue;

                if (LooksLikeFactoryGroundStation(recovered.GroundStation))
                    continue;

                loaded = recovered;
                return true;
            }
            catch
            {
                // try next candidate
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateRecoveryCandidates()
    {
        yield return SettingsPath + ".bak";
        yield return SettingsPath + ".manual-restore-IO87jp";

        var directory = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrEmpty(directory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(SettingsPath) + ".bak-*")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            yield return path;

        foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(SettingsPath) + ".restored-from-bak-*")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            yield return path;
    }

    internal static void WriteAtomic(string path, string contents)
    {
        if (string.IsNullOrWhiteSpace(contents) || contents.Length < MinimumPersistedJsonLength)
        {
            throw new InvalidOperationException(
                "Refusing to write an empty or truncated settings payload.");
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        WriteAllTextWithRetry(tempPath, contents);
        ReplaceFileWithRetry(tempPath, path);
    }

    private static void WriteAllTextWithRetry(string path, string contents)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.WriteAllText(path, contents);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static void ReplaceFileWithRetry(string sourcePath, string destinationPath)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    // Dated copy survives repeated default overwrites of settings.json.bak.
                    TryCreateTimestampedBackup(destinationPath);
                    File.Replace(
                        sourcePath,
                        destinationPath,
                        destinationPath + ".bak",
                        ignoreMetadataErrors: true);
                    PruneTimestampedBackups(destinationPath);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }

                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    internal static void TryCreateTimestampedBackup(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return;

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var backupPath = settingsPath + ".bak-" + stamp;
            if (File.Exists(backupPath))
                backupPath = settingsPath + ".bak-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8];

            File.Copy(settingsPath, backupPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("OscarWatch could not create timestamped settings backup: {0}", ex.Message);
        }
    }

    internal static void PruneTimestampedBackups(string settingsPath, int keep = MaxTimestampedBackups)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            var prefix = Path.GetFileName(settingsPath) + ".bak-";
            if (string.IsNullOrEmpty(directory) || keep < 1)
                return;

            var backups = Directory.EnumerateFiles(directory, Path.GetFileName(settingsPath) + ".bak-*")
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            foreach (var stale in backups.Skip(keep))
            {
                try
                {
                    stale.Delete();
                }
                catch
                {
                    // ignore prune failures
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("OscarWatch could not prune settings backups: {0}", ex.Message);
        }
    }

    private static void TryCopyCorruptSnapshot(string settingsPath, string json)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var snapshotPath = settingsPath + ".corrupt-" + stamp;
            File.WriteAllText(snapshotPath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("OscarWatch could not snapshot unreadable settings: {0}", ex.Message);
        }
    }

    private static void ReportSaveFailed(Exception ex)
    {
        Trace.TraceError("OscarWatch settings save failed: {0}", ex.Message);
        SaveFailed?.Invoke(ex);
    }
}
