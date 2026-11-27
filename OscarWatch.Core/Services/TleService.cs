using System.Diagnostics;
using System.Text.Json;
using OscarWatch.Core.Models;
using OscarWatch.Core.Net;
using OscarWatch.Core.Tle;

namespace OscarWatch.Core.Services;

public sealed class TleService : ITleService
{
    public const string DefaultTleUrl = TleSourceResolver.OscarWatchGpJsonUrl;

    private readonly ISettingsService? _settings;
    private readonly HttpClient _httpClient;
    private List<SatelliteCatalogEntry> _catalog = [];
    private string? _loadedSourceKey;

    public TleService(ISettingsService? settings = null, HttpClient? httpClient = null, string? cachePath = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? OscarWatchHttpClients.Create(TimeSpan.FromSeconds(30));
        CachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "tle-cache.txt");
    }

    public IReadOnlyList<SatelliteCatalogEntry> Catalog => _catalog;
    public DateTime? LastFetchedUtc { get; private set; }
    public TleCatalogLoadDiagnostics? LastLoadDiagnostics { get; private set; }

    public string CachePath { get; }

    private string CacheMetaPath => CachePath + ".meta";

    public static string BundledSeedPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "tle-seed.txt");

    public string ActiveSourceLabel => TleSourceResolver.GetDisplayLabel(EffectiveSettings);

    public bool IsStale(int staleHours) =>
        !LastFetchedUtc.HasValue ||
        DateTime.UtcNow - LastFetchedUtc.Value > TimeSpan.FromHours(staleHours);

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        var settings = EffectiveSettings;
        var sourceKey = TleSourceResolver.GetSourceKey(settings);
        if (_catalog.Count > 0 && string.Equals(_loadedSourceKey, sourceKey, StringComparison.Ordinal))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var loaded = await TryLoadFromCacheAsync(settings, allowOtherSource: false, cancellationToken).ConfigureAwait(false)
                     || (CanRefreshFromNetwork(settings)
                         && await TryRefreshFromNetworkAsync(cancellationToken).ConfigureAwait(false))
                     || (TleSourceResolver.TryGetLocalFilePath(settings) is { } localPath && TryLoadFromFile(localPath));

        if (!loaded)
        {
            // The source is unavailable (for example the server returned 403). Keep showing whatever
            // catalogue we already have rather than leaving the operator with no satellites at all.
            if (_catalog.Count == 0 && settings.Mode == TleSourceMode.OscarWatch)
                TryLoadBundledSeed();

            if (_catalog.Count == 0)
                await TryLoadFromCacheAsync(settings, allowOtherSource: true, cancellationToken).ConfigureAwait(false);
        }

        // Settle on this source even after a fallback so other callers of EnsureLoadedAsync do not
        // retry a failing download in a loop. Manual and scheduled refreshes still use RefreshAsync.
        if (_catalog.Count > 0)
            _loadedSourceKey = sourceKey;
    }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var settings = EffectiveSettings;

        if (TleSourceResolver.TryGetLocalFilePath(settings) is { } localPath)
        {
            if (!File.Exists(localPath))
                throw new FileNotFoundException($"TLE file not found: {localPath}");

            var localText = await File.ReadAllTextAsync(localPath, cancellationToken).ConfigureAwait(false);
            await ApplyCatalogTextAsync(localText, fromNetwork: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var url = TleSourceResolver.TryGetNetworkUrl(settings);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Enter a TLE download URL in Settings → TLE.");

        var text = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        await ApplyCatalogTextAsync(text, fromNetwork: true, persistCache: true, cancellationToken).ConfigureAwait(false);
    }

    public void InvalidateCatalog()
    {
        _catalog = [];
        _loadedSourceKey = null;
        LastFetchedUtc = null;
        LastLoadDiagnostics = null;

        TryDeleteCacheFile();
    }

    public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings)
    {
        var enabledIds = SatelliteCatalogMatching.CreateNoradIdSet(settings.EnabledSatelliteNoradIds);
        var enabledNames = new HashSet<string>(settings.EnabledSatelliteNames ?? [], StringComparer.OrdinalIgnoreCase);
        return _catalog.Where(s => SatelliteCatalogMatching.IsEnabled(s, enabledIds, enabledNames)).ToList();
    }

    private TleSourceSettings EffectiveSettings =>
        _settings?.Current.TleSource ?? new TleSourceSettings();

    private async Task<bool> TryLoadFromCacheAsync(
        TleSourceSettings settings,
        bool allowOtherSource,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
            return false;

        var cached = await File.ReadAllTextAsync(CachePath, cancellationToken).ConfigureAwait(false);
        var sourceKey = TleSourceResolver.GetSourceKey(settings);
        if (!await CacheMatchesCurrentSourceAsync(sourceKey, cancellationToken).ConfigureAwait(false))
        {
            // Keep the file: it is overwritten once the new source downloads, and is the only
            // fallback if that download fails.
            if (!allowOtherSource)
                return false;

            Trace.TraceWarning("TLE source unavailable; using cached catalogue from the previous source.");
        }
        else if (ShouldDiscardBuiltInTextCache(settings, cached))
        {
            Trace.TraceWarning("Discarding legacy text TLE cache for GP JSON source.");
            TryDeleteCacheFile();
            return false;
        }

        if (!TryParseCatalogText(cached, out var entries, out var diagnostics, out var failureReason))
        {
            Trace.TraceWarning("TLE cache unreadable ({0}); discarding cached file.", failureReason);
            TryDeleteCacheFile();
            return false;
        }

        _catalog = entries.ToList();
        LastFetchedUtc = File.GetLastWriteTimeUtc(CachePath);
        RecordSuccessfulLoad(TleLoadOrigin.Cache, diagnostics);
        return true;
    }

    private async Task<bool> TryRefreshFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return _catalog.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as TaskCanceledException even when the caller
            // did not cancel. Treat that as a failed download so the bundled seed or cache can load.
            Trace.TraceWarning("TLE network refresh failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task ApplyCatalogTextAsync(
        string text,
        bool fromNetwork,
        bool persistCache = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCatalogText(text, out var entries, out var diagnostics, out var failureReason))
            throw new InvalidOperationException($"TLE data could not be parsed: {failureReason}");

        var sourceKey = TleSourceResolver.GetSourceKey(EffectiveSettings);
        if (persistCache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, text, cancellationToken).ConfigureAwait(false);
            await WriteCacheMetaAsync(sourceKey, cancellationToken).ConfigureAwait(false);
        }

        _catalog = entries.ToList();
        _loadedSourceKey = sourceKey;
        LastFetchedUtc = fromNetwork
            ? DateTime.UtcNow
            : persistCache && File.Exists(CachePath)
                ? File.GetLastWriteTimeUtc(CachePath)
                : DateTime.UtcNow;
        RecordSuccessfulLoad(
            fromNetwork ? TleLoadOrigin.Network : TleLoadOrigin.LocalFile,
            diagnostics);
    }

    private async Task<bool> CacheMatchesCurrentSourceAsync(string sourceKey, CancellationToken cancellationToken)
    {
        if (!File.Exists(CacheMetaPath))
            return true;

        var stored = (await File.ReadAllTextAsync(CacheMetaPath, cancellationToken).ConfigureAwait(false)).Trim();
        return string.Equals(stored, sourceKey, StringComparison.Ordinal);
    }

    private async Task WriteCacheMetaAsync(string sourceKey, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(CacheMetaPath, sourceKey, cancellationToken).ConfigureAwait(false);

    private static bool TryParseCatalogText(
        string text,
        out IReadOnlyList<SatelliteCatalogEntry> entries,
        out TleCatalogParseDiagnostics diagnostics,
        out string failureReason)
    {
        entries = [];
        diagnostics = TleCatalogParseDiagnostics.Empty;
        failureReason = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            failureReason = "file is empty";
            return false;
        }

        try
        {
            var result = TleCatalogParser.ParseCatalogWithDiagnostics(text);
            entries = result.Entries;
            diagnostics = result.Diagnostics;
            if (entries.Count > 0)
                return true;

            failureReason = diagnostics.SkippedOrbitalSanity > 0
                ? "no satellites passed orbital sanity checks"
                : "no usable satellites";
            return false;
        }
        catch (JsonException ex)
        {
            failureReason = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private void RecordSuccessfulLoad(TleLoadOrigin origin, TleCatalogParseDiagnostics diagnostics)
    {
        LastLoadDiagnostics = new TleCatalogLoadDiagnostics(
            origin,
            diagnostics.ParsedCount,
            diagnostics.SkippedIncomplete,
            diagnostics.SkippedOrbitalSanity);
    }

    private bool TryLoadFromFile(string path)
    {
        if (!File.Exists(path))
            return false;

        var text = File.ReadAllText(path);
        if (!TryParseCatalogText(text, out var entries, out var diagnostics, out _))
            return false;

        _catalog = entries.ToList();
        LastFetchedUtc = File.GetLastWriteTimeUtc(path);
        RecordSuccessfulLoad(TleLoadOrigin.LocalFile, diagnostics);
        return true;
    }

    private static bool ShouldDiscardBuiltInTextCache(TleSourceSettings settings, string cached)
    {
        if (settings.Mode is not (TleSourceMode.OscarWatch or TleSourceMode.AmsatOrg))
            return false;

        var trimmed = cached.TrimStart();
        return !trimmed.StartsWith("[", StringComparison.Ordinal) && !trimmed.StartsWith("{", StringComparison.Ordinal);
    }

    private static bool CanRefreshFromNetwork(TleSourceSettings settings) =>
        TleSourceResolver.TryGetNetworkUrl(settings) is not null
        && TleSourceResolver.TryGetLocalFilePath(settings) is null;

    private void TryLoadBundledSeed()
    {
        if (!File.Exists(BundledSeedPath))
            return;

        var text = File.ReadAllText(BundledSeedPath);
        if (!TryParseCatalogText(text, out var entries, out var diagnostics, out _))
            return;

        _catalog = entries.ToList();
        if (_catalog.Count > 0)
        {
            LastFetchedUtc = null;
            RecordSuccessfulLoad(TleLoadOrigin.BundledSeed, diagnostics);
        }
    }

    private void TryDeleteCacheFile()
    {
        foreach (var path in new[] { CachePath, CacheMetaPath })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                File.Delete(path);
            }
            catch
            {
                // best effort — next refresh will overwrite
            }
        }
    }
}
