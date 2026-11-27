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

        if (IsUsingUserDatabase && File.Exists(_bundledPath))
            BackfillMissingPassbandBoundsFromBundled(_entries, _bundledPath);

        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var trimmedName = entry.Name.Trim();
            _byName[trimmedName] = entry;
            RegisterParentheticalAlias(trimmedName);
            
            if (entry.AlternativeNames != null)
            {
                foreach (var alias in entry.AlternativeNames)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    var trimmedAlias = alias.Trim();
                    _byName.TryAdd(trimmedAlias, entry);
                    RegisterParentheticalAlias(trimmedAlias);
                }
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
        var parenIndex = name.IndexOf('(');
        if (parenIndex > 0)
        {
            var prefixSpan = name.AsSpan(0, parenIndex).TrimEnd();
            if (prefixSpan.Length > 0)
            {
                var prefix = prefixSpan.ToString();
                if (!_byName.ContainsKey(prefix))
                    _byName[prefix] = _byName[name];
            }
        }
    }

    private SatelliteRadioEntry? ResolveEntry(string trimmed)
    {
        if (_byName.TryGetValue(trimmed, out var exact))
            return exact;

        if (StaticAliases.TryGetValue(trimmed, out var aliasKey)
            && _byName.TryGetValue(aliasKey, out var byAlias))
            return byAlias;

        var parenIndex = trimmed.IndexOf('(');
        if (parenIndex > 0)
        {
            var prefix = trimmed.AsSpan(0, parenIndex);
            // Trim the prefix span and convert to string only if needed
            var trimmedPrefix = prefix.TrimEnd().ToString();
            if (_byName.TryGetValue(trimmedPrefix, out var byPrefix))
                return byPrefix;
        }

        var normalized = NormalizeName(trimmed);
        return _byNormalizedName.GetValueOrDefault(normalized);
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Pre-allocate buffer for worst case (no chars removed)
        Span<char> buffer = stackalloc char[name.Length];
        var writeIndex = 0;

        foreach (var c in name.AsSpan())
        {
            if (c != ' ' && c != '-')
                buffer[writeIndex++] = char.ToUpperInvariant(c);
        }

        return buffer[..writeIndex].ToString();
    }

    /// <summary>
    /// Existing users with a customised user database will not yet have passband bounds
    /// for satellites that gained them in a newer bundled database. Fill in missing bounds
    /// in memory without overwriting any bounds the user explicitly configured.
    /// </summary>
    private static void BackfillMissingPassbandBoundsFromBundled(
        List<SatelliteRadioEntry> userEntries,
        string bundledPath)
    {
        var bundledEntries = SatelliteDatabaseFile.Load(bundledPath);
        if (bundledEntries.Count == 0)
            return;

        var bundledByNorad = new Dictionary<string, SatelliteRadioEntry>(StringComparer.Ordinal);
        var bundledByName = new Dictionary<string, SatelliteRadioEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundled in bundledEntries)
        {
            if (!HasAnyPassbandBounds(bundled))
                continue;

            var norad = CanonicalNoradId(bundled.NoradId);
            if (norad is not null)
                bundledByNorad.TryAdd(norad, bundled);

            if (!string.IsNullOrWhiteSpace(bundled.Name))
                bundledByName.TryAdd(bundled.Name.Trim(), bundled);
        }

        if (bundledByNorad.Count == 0 && bundledByName.Count == 0)
            return;

        foreach (var user in userEntries)
        {
            SatelliteRadioEntry? match = null;
            var userNorad = CanonicalNoradId(user.NoradId);
            if (userNorad is not null)
                bundledByNorad.TryGetValue(userNorad, out match);

            if (match is null && !string.IsNullOrWhiteSpace(user.Name))
                bundledByName.TryGetValue(user.Name.Trim(), out match);

            if (match is null)
                continue;

            if (!user.HasDownlinkPassband && match.HasDownlinkPassband)
            {
                user.DownlinkMinKHz = match.DownlinkMinKHz;
                user.DownlinkMaxKHz = match.DownlinkMaxKHz;
            }

            if (!user.HasUplinkPassband && match.HasUplinkPassband)
            {
                user.UplinkMinKHz = match.UplinkMinKHz;
                user.UplinkMaxKHz = match.UplinkMaxKHz;
            }

            BackfillMissingModePassbandBounds(user, match);
        }
    }

    private static bool HasAnyPassbandBounds(SatelliteRadioEntry entry) =>
        entry.HasDownlinkPassband
        || entry.HasUplinkPassband
        || entry.Modes.Any(mode => mode.HasDownlinkPassband || mode.HasUplinkPassband);

    /// <summary>
    /// AO-07 keeps one passband per transponder (Mode A on 10 m, Mode B on 2 m).
    /// Copy those mode edges when the user copy has none, and leave edges the operator set.
    /// </summary>
    private static void BackfillMissingModePassbandBounds(SatelliteRadioEntry user, SatelliteRadioEntry bundled)
    {
        foreach (var bundledMode in bundled.Modes)
        {
            if (!bundledMode.HasDownlinkPassband && !bundledMode.HasUplinkPassband)
                continue;

            var userMode = user.Modes.FirstOrDefault(mode =>
                string.Equals(mode.Type, bundledMode.Type, StringComparison.OrdinalIgnoreCase));
            if (userMode is null)
                continue;

            if (!userMode.HasDownlinkPassband && bundledMode.HasDownlinkPassband)
            {
                userMode.DownlinkMinKHz = bundledMode.DownlinkMinKHz;
                userMode.DownlinkMaxKHz = bundledMode.DownlinkMaxKHz;
            }

            if (!userMode.HasUplinkPassband && bundledMode.HasUplinkPassband)
            {
                userMode.UplinkMinKHz = bundledMode.UplinkMinKHz;
                userMode.UplinkMaxKHz = bundledMode.UplinkMaxKHz;
            }
        }
    }
}
