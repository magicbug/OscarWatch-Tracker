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

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Timer? _saveTimer;
    private volatile bool _savePending;
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

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        if (!File.Exists(SettingsPath))
        {
            Current = new AppSettings();
            SyncGridFromLatLon();
            EnsureSavedStations();
            SaveToDisk();
            return;
        }

        var json = File.ReadAllText(SettingsPath);
        if (!TryParse(json, out var settings, out _))
            settings = new AppSettings();

        Current = settings;
        EnsureSavedStations();

        if (string.IsNullOrWhiteSpace(Current.GroundStation.GridSquare))
            SyncGridFromLatLon();
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
        settings.QsoLogbook ??= new QsoLogbookSettings();
        settings.QsoLogbook.HistoryColumnWidthsPx ??=
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        settings.TleSource ??= new TleSourceSettings();
        settings.TleSource = TleSourceResolver.NormalizeLegacyCustomUrl(settings.TleSource);
        settings.TransponderConflictAcknowledgments ??= [];
        settings.SatellitePriorities ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        WriteAtomic(SettingsPath, json);
    }

    internal static void WriteAtomic(string path, string contents)
    {
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
                    File.Replace(sourcePath, destinationPath, destinationPath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(sourcePath, destinationPath);

                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static void ReportSaveFailed(Exception ex)
    {
        Trace.TraceError("OscarWatch settings save failed: {0}", ex.Message);
        SaveFailed?.Invoke(ex);
    }
}
