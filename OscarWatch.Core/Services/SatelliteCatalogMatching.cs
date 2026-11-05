using OscarWatch.Core.Models;
using OscarWatch.Core.Tle;

namespace OscarWatch.Core.Services;

public static class SatelliteCatalogMatching
{
    /// <summary>
    /// Pre-computed index of the text before the brackets and the alias inside them.
    /// If enabled contains "ISS (ZARYA)", the index contains "ISS" and "ZARYA".
    /// </summary>
    private static Dictionary<string, bool>? _parenthesisIndex;

    /// <summary>
    /// Reference to the enabled set used to build <see cref="_parenthesisIndex"/>.
    /// When the enabled set instance changes, the index is rebuilt.
    /// </summary>
    private static IReadOnlySet<string>? _lastEnabledSet;

    /// <summary>
    /// Size of <see cref="_lastEnabledSet"/> when the index was built.
    /// Migrate appends names to the same set, and a count change forces a rebuild.
    /// </summary>
    private static int _lastEnabledCount;

    private static readonly object ParenthesisIndexLock = new();

    /// <summary>
    /// True when the satellite is enabled by normalised catalogue ID, or else by name alias rules.
    /// </summary>
    public static bool IsEnabled(
        SatelliteCatalogEntry satellite,
        IReadOnlySet<string> enabledNoradIds,
        IReadOnlySet<string> enabledNames)
    {
        if (MatchesNoradId(satellite.NoradId, enabledNoradIds))
            return true;

        return IsEnabledByName(satellite, enabledNames);
    }

    /// <summary>Name-only enablement (legacy / migrate source). Prefer the ID+name overload.</summary>
    public static bool IsEnabled(SatelliteCatalogEntry satellite, IReadOnlySet<string> enabledNames) =>
        IsEnabledByName(satellite, enabledNames);

    private static bool IsEnabledByName(SatelliteCatalogEntry satellite, IReadOnlySet<string> enabled)
    {
        // O(1) exact match via case-insensitive HashSet
        if (enabled.Contains(satellite.Name))
            return true;

        // Enabled "ISS (ZARYA)" or "SAUDISAT 1C (SO-50)" also selects the prefix
        // ("ISS", "SAUDISAT 1C") and the alias inside the brackets ("ZARYA", "SO-50").
        // A space or hyphen is not an alias boundary, so "METEOR-M2" does not select
        // "METEOR-M2 2" and "METEOR-M N2" does not select "METEOR-M N2-3".
        var index = GetOrBuildParenthesisIndex(enabled);
        if (index.Count > 0 && index.ContainsKey(satellite.Name))
            return true;

        // Catalogue "ISS (ZARYA)" is selected when the operator stored "ISS" or "ZARYA".
        if (!TrySplitParenthetical(satellite.Name, out var prefix, out var alias))
            return false;

        return (prefix.Length > 0 && enabled.Contains(prefix))
            || (alias.Length > 0 && enabled.Contains(alias));
    }

    /// <summary>
    /// Splits <paramref name="name"/> into the text before the first brackets and the alias inside them.
    /// </summary>
    internal static bool TrySplitParenthetical(string name, out string prefix, out string alias)
    {
        prefix = "";
        alias = "";
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var openParen = name.IndexOf('(');
        var closeParen = name.IndexOf(')');
        if (openParen < 0 || closeParen <= openParen + 1)
            return false;

        prefix = name[..openParen].Trim();
        alias = name[(openParen + 1)..closeParen].Trim();
        return prefix.Length > 0 || alias.Length > 0;
    }

    /// <summary>
    /// Builds a case-insensitive set of normalised catalogue IDs (Alpha-5 / D5).
    /// </summary>
    public static HashSet<string> CreateNoradIdSet(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids is null)
            return set;

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            set.Add(NormalizeNoradId(id));
        }

        return set;
    }

    public static bool MatchesNoradId(string? noradId, IReadOnlySet<string> enabledNoradIds)
    {
        if (string.IsNullOrWhiteSpace(noradId) || enabledNoradIds.Count == 0)
            return false;

        return enabledNoradIds.Contains(NormalizeNoradId(noradId));
    }

    public static string NormalizeNoradId(string noradId)
    {
        var trimmed = noradId.Trim();
        return Alpha5CatalogId.Normalize(trimmed) ?? trimmed;
    }

    /// <summary>
    /// Appends normalised catalogue IDs for name-matched satellites that are missing from
    /// <see cref="AppSettings.EnabledSatelliteNoradIds"/>. Does not remove names.
    /// Also normalises any existing ID spellings (e.g. 100000 → A0000).
    /// </summary>
    public static bool TryMigrateEnabledSatelliteIds(
        AppSettings settings,
        IReadOnlyList<SatelliteCatalogEntry> catalog)
    {
        settings.EnabledSatelliteNames ??= [];
        settings.EnabledSatelliteNoradIds ??= [];

        var changed = false;
        var normalisedIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in settings.EnabledSatelliteNoradIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                changed = true;
                continue;
            }

            var normalised = NormalizeNoradId(id);
            if (!seenIds.Add(normalised))
            {
                changed = true;
                continue;
            }

            if (!string.Equals(id.Trim(), normalised, StringComparison.Ordinal))
                changed = true;

            normalisedIds.Add(normalised);
        }

        if (normalisedIds.Count != settings.EnabledSatelliteNoradIds.Count)
            changed = true;

        var nameSet = new HashSet<string>(settings.EnabledSatelliteNames, StringComparer.OrdinalIgnoreCase);

        foreach (var sat in catalog)
        {
            if (!IsEnabledByName(sat, nameSet))
                continue;

            if (string.IsNullOrWhiteSpace(sat.NoradId))
                continue;

            var normalised = NormalizeNoradId(sat.NoradId);
            if (!seenIds.Add(normalised))
                continue;

            normalisedIds.Add(normalised);
            changed = true;

            if (!nameSet.Contains(sat.Name))
            {
                settings.EnabledSatelliteNames.Add(sat.Name);
                nameSet.Add(sat.Name);
                changed = true;
            }
        }

        if (changed)
            settings.EnabledSatelliteNoradIds = normalisedIds;

        return changed;
    }

    /// <summary>
    /// Returns the parenthesis alias index, rebuilding it only if the enabled set instance changes.
    /// </summary>
    private static Dictionary<string, bool> GetOrBuildParenthesisIndex(IReadOnlySet<string> enabled)
    {
        lock (ParenthesisIndexLock)
        {
            if (ReferenceEquals(_lastEnabledSet, enabled)
                && _parenthesisIndex is not null
                && _lastEnabledCount == enabled.Count)
                return _parenthesisIndex;

            var index = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in enabled)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!TrySplitParenthetical(name, out var prefix, out var alias))
                    continue;

                if (prefix.Length > 0)
                    index[prefix] = true;
                if (alias.Length > 0)
                    index[alias] = true;
            }

            _parenthesisIndex = index;
            _lastEnabledSet = enabled;
            _lastEnabledCount = enabled.Count;
            return _parenthesisIndex;
        }
    }

    /// <summary>
    /// Clears the cached index. Used by tests to ensure isolation.
    /// </summary>
    internal static void ResetCache()
    {
        lock (ParenthesisIndexLock)
        {
            _parenthesisIndex = null;
            _lastEnabledSet = null;
            _lastEnabledCount = 0;
        }
    }
}
