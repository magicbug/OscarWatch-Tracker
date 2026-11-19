using System.Collections.Concurrent;
using OscarWatch.Core.Net;

namespace OscarWatch.Core.Logbook;

/// <summary>One park, summit, or reference from a programme directory.</summary>
/// <param name="Reference">Reference as written in ADIF, for example US-1356, W3/SV-001, or KFF-4655.</param>
/// <param name="Name">Park, summit, or reference name.</param>
/// <param name="Detail">Location text: POTA location, SOTA association and region, or WWFF country.</param>
public sealed record ActivationDirectoryEntry(string Reference, string Name, string Detail)
{
    /// <summary>The reference, so a suggestion list fills the text box with the reference.</summary>
    public override string ToString() => Reference;
}

/// <summary>
/// Caches the official POTA, SOTA, and WWFF directories under the app data folder so the
/// reference boxes can search them offline. A cache older than <see cref="MaxCacheAge"/> is
/// refreshed on the next load. A failed download keeps the previous cache.
/// </summary>
public sealed class ActivationDirectoryService
{
    /// <summary>How long a downloaded directory is trusted before it is fetched again.</summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(21);

    private const int MaxSuggestionsDefault = 30;

    private static readonly IReadOnlyDictionary<ActivationProgramme, string[]> Urls =
        new Dictionary<ActivationProgramme, string[]>
        {
            [ActivationProgramme.Pota] =
            [
                "https://pota.app/all_parks_ext.csv",
                "https://pota.us/all_parks_ext.csv"
            ],
            [ActivationProgramme.Sota] =
            [
                "https://storage.sota.org.uk/summitslist.csv",
                "https://www.sotadata.org.uk/summitslist.csv"
            ],
            [ActivationProgramme.Wwff] =
            [
                "https://wwff.co/wwff-data/wwff_directory.csv"
            ]
        };

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<ActivationProgramme, Snapshot> _loaded = new();
    private readonly ConcurrentDictionary<ActivationProgramme, Task> _inFlight = new();
    private readonly ConcurrentDictionary<ActivationProgramme, string> _lastError = new();

    public ActivationDirectoryService(string? cacheFolder = null, HttpClient? httpClient = null)
    {
        CacheFolder = cacheFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OscarWatch",
            "activation-directories");
        _httpClient = httpClient ?? OscarWatchHttpClients.Create(TimeSpan.FromMinutes(5));
    }

    public string CacheFolder { get; }

    /// <summary>True once the directory has been read from the cache or downloaded in this session.</summary>
    public bool IsLoaded(ActivationProgramme programme) => _loaded.ContainsKey(programme);

    /// <summary>When the cached copy was last written, or null when there is no cache.</summary>
    public DateTime? LastUpdatedUtc(ActivationProgramme programme)
    {
        var path = CachePath(programme);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    /// <summary>Message from the last failed download, or empty when the last attempt succeeded or none was made.</summary>
    public string LastError(ActivationProgramme programme) =>
        _lastError.TryGetValue(programme, out var error) ? error : "";

    public string CachePath(ActivationProgramme programme) =>
        Path.Combine(CacheFolder, programme.ToString().ToLowerInvariant() + ".tsv");

    /// <summary>
    /// Makes the directory available for <see cref="Search"/>. Reads the cache first, and downloads
    /// when there is no cache or it is older than <see cref="MaxCacheAge"/>. Never throws for network
    /// problems; check <see cref="LastError"/> instead.
    /// </summary>
    public async Task EnsureLoadedAsync(ActivationProgramme programme, CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(programme, out var loaded) && IsFresh(loaded.WrittenUtc))
            return;

        var cached = LoadCacheIfPresent(programme);
        if (cached is not null && IsFresh(cached.WrittenUtc))
            return;

        await RefreshAsync(programme, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads the directory now and replaces the cache on success. Concurrent callers share one download.</summary>
    public async Task RefreshAsync(ActivationProgramme programme, CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(programme, out task!) || task.IsCompleted)
            {
                task = DownloadAsync(programme);
                _inFlight[programme] = task;
            }
        }

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsFresh(DateTime writtenUtc) => DateTime.UtcNow - writtenUtc < MaxCacheAge;

    /// <summary>
    /// Entries whose reference, name, or location contains <paramref name="query"/>. Reference
    /// prefix matches come first. Returns nothing when the directory is not loaded.
    /// </summary>
    public IReadOnlyList<ActivationDirectoryEntry> Search(
        ActivationProgramme programme,
        string? query,
        int limit = MaxSuggestionsDefault)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0 || !_loaded.TryGetValue(programme, out var snapshot))
            return [];

        var upper = needle.ToUpperInvariant();
        var prefix = new List<ActivationDirectoryEntry>();
        var contains = new List<ActivationDirectoryEntry>();
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Reference.StartsWith(upper, StringComparison.OrdinalIgnoreCase))
            {
                prefix.Add(entry);
                if (prefix.Count >= limit)
                    break;
                continue;
            }

            if (contains.Count < limit
                && (entry.Reference.Contains(upper, StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                contains.Add(entry);
            }
        }

        if (prefix.Count >= limit)
            return prefix;

        prefix.AddRange(contains.Take(limit - prefix.Count));
        return prefix;
    }

    /// <summary>Number of entries currently available for search.</summary>
    public int Count(ActivationProgramme programme) =>
        _loaded.TryGetValue(programme, out var snapshot) ? snapshot.Entries.Count : 0;

    private async Task DownloadAsync(ActivationProgramme programme)
    {
        Exception? last = null;
        foreach (var url in Urls[programme])
        {
            try
            {
                var entries = await FetchAsync(programme, url, CancellationToken.None).ConfigureAwait(false);
                if (entries.Count == 0)
                    throw new InvalidDataException($"No entries in {url}.");

                WriteCache(programme, entries);
                _loaded[programme] = new Snapshot(entries, File.GetLastWriteTimeUtc(CachePath(programme)));
                _lastError[programme] = "";
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                last = ex;
            }
        }

        // Keep whatever is cached. The UI reports the error and still accepts typed references.
        _lastError[programme] = last?.Message ?? "Download failed.";
        if (!_loaded.ContainsKey(programme))
            LoadCacheIfPresent(programme);
    }

    private async Task<List<ActivationDirectoryEntry>> FetchAsync(
        ActivationProgramme programme,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return programme switch
        {
            ActivationProgramme.Pota => ParsePota(reader),
            ActivationProgramme.Sota => ParseSota(reader),
            ActivationProgramme.Wwff => ParseWwff(reader),
            _ => throw new ArgumentOutOfRangeException(nameof(programme))
        };
    }

    private Snapshot? LoadCacheIfPresent(ActivationProgramme programme)
    {
        var path = CachePath(programme);
        if (!File.Exists(path))
            return null;

        try
        {
            var entries = new List<ActivationDirectoryEntry>();
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0)
                    continue;
                entries.Add(new ActivationDirectoryEntry(
                    parts[0],
                    parts[1],
                    parts.Length > 2 ? parts[2] : ""));
            }

            if (entries.Count == 0)
                return null;

            var snapshot = new Snapshot(entries, File.GetLastWriteTimeUtc(path));
            _loaded[programme] = snapshot;
            return snapshot;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteCache(ActivationProgramme programme, IReadOnlyList<ActivationDirectoryEntry> entries)
    {
        Directory.CreateDirectory(CacheFolder);
        var path = CachePath(programme);
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, append: false))
        {
            foreach (var entry in entries)
            {
                writer.Write(Clean(entry.Reference));
                writer.Write('\t');
                writer.Write(Clean(entry.Name));
                writer.Write('\t');
                writer.Write(Clean(entry.Detail));
                writer.Write('\n');
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string Clean(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>POTA all_parks_ext.csv: reference, name, active, entityId, locationDesc, latitude, longitude, grid.</summary>
    public static List<ActivationDirectoryEntry> ParsePota(TextReader reader)
    {
        var header = ReadHeader(reader, "reference");
        var iRef = header["reference"];
        var iName = IndexOf(header, "name");
        var iActive = IndexOf(header, "active");
        var iLocation = IndexOf(header, "locationdesc");

        var list = new List<ActivationDirectoryEntry>();
        foreach (var fields in ReadRecords(reader))
        {
            if (iActive >= 0 && Field(fields, iActive) == "0")
                continue;
            var reference = Field(fields, iRef).ToUpperInvariant();
            if (reference.Length == 0)
                continue;
            list.Add(new ActivationDirectoryEntry(reference, Field(fields, iName), Field(fields, iLocation)));
        }

        return list;
    }

    /// <summary>SOTA summitslist.csv: an optional title line, then SummitCode, AssociationName, RegionName, SummitName, ...</summary>
    public static List<ActivationDirectoryEntry> ParseSota(TextReader reader)
    {
        var header = ReadHeader(reader, "summitcode");
        var iRef = header["summitcode"];
        var iName = IndexOf(header, "summitname");
        var iAssociation = IndexOf(header, "associationname");
        var iRegion = IndexOf(header, "regionname");

        var list = new List<ActivationDirectoryEntry>();
        foreach (var fields in ReadRecords(reader))
        {
            var reference = Field(fields, iRef).ToUpperInvariant();
            if (reference.Length == 0)
                continue;

            var association = Field(fields, iAssociation);
            var region = Field(fields, iRegion);
            var detail = region.Length == 0 || string.Equals(region, association, StringComparison.OrdinalIgnoreCase)
                ? association
                : association + " / " + region;
            list.Add(new ActivationDirectoryEntry(reference, Field(fields, iName), detail));
        }

        return list;
    }

    /// <summary>WWFF wwff_directory.csv: reference, status, name, ..., country. Only active rows are kept.</summary>
    public static List<ActivationDirectoryEntry> ParseWwff(TextReader reader)
    {
        var header = ReadHeader(reader, "reference");
        var iRef = header["reference"];
        var iStatus = IndexOf(header, "status");
        var iName = IndexOf(header, "name");
        var iCountry = IndexOf(header, "country");

        var list = new List<ActivationDirectoryEntry>();
        foreach (var fields in ReadRecords(reader))
        {
            if (iStatus >= 0 && !string.Equals(Field(fields, iStatus), "active", StringComparison.OrdinalIgnoreCase))
                continue;
            var reference = Field(fields, iRef).ToUpperInvariant();
            if (reference.Length == 0)
                continue;
            list.Add(new ActivationDirectoryEntry(reference, Field(fields, iName), Field(fields, iCountry)));
        }

        return list;
    }

    /// <summary>Reads lines until the header containing <paramref name="requiredColumn"/>, skipping any title lines before it.</summary>
    private static Dictionary<string, int> ReadHeader(TextReader reader, string requiredColumn)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var columns = SplitCsvLine(line);
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                map[columns[i].Trim().ToLowerInvariant()] = i;

            if (map.ContainsKey(requiredColumn))
                return map;
        }

        throw new InvalidDataException($"Directory header with '{requiredColumn}' not found.");
    }

    private static IEnumerable<List<string>> ReadRecords(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            yield return SplitCsvLine(line);
        }
    }

    private static int IndexOf(Dictionary<string, int> header, string column) =>
        header.TryGetValue(column, out var index) ? index : -1;

    private static string Field(List<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : "";

    /// <summary>Splits one CSV line, honouring double-quoted fields with embedded commas and doubled quotes.</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private sealed record Snapshot(IReadOnlyList<ActivationDirectoryEntry> Entries, DateTime WrittenUtc);
}
