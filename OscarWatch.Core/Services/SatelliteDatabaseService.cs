using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public sealed class SatelliteDatabaseService : ISatelliteDatabaseService
{
    private static readonly Dictionary<string, string> StaticAliases =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _bundledPath;
    private readonly string _userPath;
    private Dictionary<string, SatelliteRadioEntry> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SatelliteRadioEntry> _byNormalizedName =
        new(StringComparer.Ordinal);

    private Dictionary<string, SatelliteRadioEntry> _byNoradId =
        new(StringComparer.Ordinal);

    private List<SatelliteRadioEntry> _entries = [];

    public IReadOnlyList<SatelliteRadioEntry> Entries => _entries;

    public string ActiveDatabasePath => File.Exists(_userPath) ? _userPath : _bundledPath;

    public bool IsUsingUserDatabase => File.Exists(_userPath);

    static SatelliteDatabaseService()
    {
        RegisterStaticAlias("AO-7", "AO-07");
        RegisterStaticAlias("AO-07 (OSCAR 7)", "AO-07");
        RegisterStaticAlias("ISS (ZARYA)", "ISS");
        RegisterStaticAlias("ZARYA", "ISS");
        RegisterStaticAlias("FOX-1B", "RADFXSAT (FOX-1B)");
        RegisterStaticAlias("RADFXSAT", "RADFXSAT (FOX-1B)");
        RegisterStaticAlias("PO-101 (DIWATA2)", "PO-101");
        RegisterStaticAlias("DIWATA2", "PO-101");
        RegisterStaticAlias("SAUDISAT 1C", "SO-50");
        RegisterStaticAlias("OSCAR 7", "AO-07");
        RegisterStaticAlias("OSCAR 50", "SO-50");
    }

    public SatelliteDatabaseService(string bundledPath, string? userPath = null)
    {
        _bundledPath = bundledPath;
        _userPath = userPath ?? SatelliteDatabasePaths.UserDatabasePath;
        Reload();
    }

    public void Reload()
    {
        _byName = new Dictionary<string, SatelliteRadioEntry>(StringComparer.OrdinalIgnoreCase);
        _entries = File.Exists(ActiveDatabasePath)
            ? SatelliteDatabaseFile.Load(ActiveDatabasePath)
            : [];

        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            _byName[entry.Name.Trim()] = entry;
            RegisterParentheticalAlias(entry.Name.Trim());
            foreach (var alias in entry.AlternativeNames ?? [])
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                var trimmedAlias = alias.Trim();
                _byName.TryAdd(trimmedAlias, entry);
                RegisterParentheticalAlias(trimmedAlias);
            }
        }

        _byNormalizedName = new Dictionary<string, SatelliteRadioEntry>(_byName.Count, StringComparer.Ordinal);
        foreach (var (key, entry) in _byName)
            _byNormalizedName.TryAdd(NormalizeName(key), entry);

        _byNoradId = new Dictionary<string, SatelliteRadioEntry>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            var noradId = CanonicalNoradId(entry.NoradId);
            if (noradId is not null)
                _byNoradId.TryAdd(noradId, entry);
        }
    }

    public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null)
    {
        if (!string.IsNullOrWhiteSpace(satelliteName))
        {
            var byName = ResolveEntry(satelliteName.Trim());
            if (byName is not null)
                return byName;
        }

        return TryGetEntryByNoradId(noradId);
    }

    private SatelliteRadioEntry? TryGetEntryByNoradId(string? noradId)
    {
        var canonical = CanonicalNoradId(noradId);
        if (canonical is null)
            return null;

        return _byNoradId.GetValueOrDefault(canonical);
    }

    private static string? CanonicalNoradId(string? noradId) =>
        SatelliteDatabaseFile.NormalizeNoradId(noradId) is { } normalized
        && SatelliteDatabaseFile.IsValidNoradId(normalized)
            ? normalized
            : null;

    private static void RegisterStaticAlias(string tleName, string databaseName) =>
        StaticAliases[tleName] = databaseName;

    private void RegisterParentheticalAlias(string name)
    {
        var paren = name.IndexOf('(');
        if (paren > 0)
        {
            var prefix = name[..paren].Trim();
            if (!_byName.ContainsKey(prefix))
                _byName[prefix] = _byName[name];
        }
    }

    private SatelliteRadioEntry? ResolveEntry(string trimmed)
    {
        if (_byName.TryGetValue(trimmed, out var exact))
            return exact;

        if (StaticAliases.TryGetValue(trimmed, out var aliasKey)
            && _byName.TryGetValue(aliasKey, out var byAlias))
            return byAlias;

        var paren = trimmed.IndexOf('(');
        if (paren > 0)
        {
            var prefix = trimmed[..paren].Trim();
            if (_byName.TryGetValue(prefix, out var byPrefix))
                return byPrefix;
        }

        var normalized = NormalizeName(trimmed);
        return _byNormalizedName.GetValueOrDefault(normalized);
    }

    private static string NormalizeName(string name) =>
        name.Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToUpperInvariant();
}
