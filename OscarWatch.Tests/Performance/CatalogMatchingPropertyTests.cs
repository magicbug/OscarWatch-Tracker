// Feature: startup-io-rendering-optimisation, Property 7: Optimised catalog matching preserves original semantics

using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests.Performance;

/// <summary>
/// Property 7: For any SatelliteCatalogEntry and for any enabled name set, the optimised
/// IsEnabled SHALL return the same boolean result as the original linear-scan implementation.
///
/// **Validates: Requirements 5.2, 5.3, 5.4**
/// </summary>
public class CatalogMatchingPropertyTests : IDisposable
{
    public void Dispose()
    {
        SatelliteCatalogMatching.ResetCache();
    }

    /// <summary>
    /// Linear scan of the same bracketed-alias rules, without the parenthesis index.
    /// Used as the oracle to verify the indexed lookup produces identical results.
    /// </summary>
    private static bool ReferenceIsEnabled(string satelliteName, IReadOnlySet<string> enabled)
    {
        if (enabled.Contains(satelliteName))
            return true;

        foreach (var name in enabled)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!SatelliteCatalogMatching.TrySplitParenthetical(name, out var prefix, out var alias))
                continue;

            if (prefix.Length > 0 && satelliteName.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (alias.Length > 0 && satelliteName.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!SatelliteCatalogMatching.TrySplitParenthetical(satelliteName, out var satPrefix, out var satAlias))
            return false;

        return (satPrefix.Length > 0 && enabled.Contains(satPrefix))
            || (satAlias.Length > 0 && enabled.Contains(satAlias));
    }

    /// <summary>
    /// Property test: optimised IsEnabled matches the reference implementation for arbitrary
    /// satellite names and enabled sets.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Optimised_matches_reference_for_arbitrary_inputs(NonEmptyString rawSatName, string[] rawEnabledNames)
    {
        SatelliteCatalogMatching.ResetCache();

        var satName = rawSatName.Get;
        // Filter to non-null enabled names (FsCheck may produce nulls in arrays)
        var enabledList = (rawEnabledNames ?? []).Where(n => n is not null).ToArray();
        var enabled = new HashSet<string>(enabledList, StringComparer.OrdinalIgnoreCase);

        var satellite = new SatelliteCatalogEntry
        {
            Name = satName,
            NoradId = "99999",
            Line1 = "1 99999U 00000A   24001.00000000  .00000000  00000-0  00000-0 0  0000",
            Line2 = "2 99999  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
        };

        var optimisedResult = SatelliteCatalogMatching.IsEnabled(satellite, enabled);
        var referenceResult = ReferenceIsEnabled(satName, enabled);

        return optimisedResult == referenceResult;
    }

    /// <summary>
    /// Property test: exact match via HashSet is O(1) — when the satellite name is in the
    /// enabled set, IsEnabled always returns true.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Exact_match_always_returns_true(NonEmptyString rawName)
    {
        SatelliteCatalogMatching.ResetCache();

        var name = rawName.Get;
        var enabled = new HashSet<string>(new[] { name }, StringComparer.OrdinalIgnoreCase);

        var satellite = new SatelliteCatalogEntry
        {
            Name = name,
            NoradId = "99999",
            Line1 = "1 99999U 00000A   24001.00000000  .00000000  00000-0  00000-0 0  0000",
            Line2 = "2 99999  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
        };

        return SatelliteCatalogMatching.IsEnabled(satellite, enabled);
    }

    /// <summary>
    /// Property test: parenthesised alias matching — when an enabled name contains a
    /// parenthesised alias, the alias alone matches as a satellite name.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Parenthesis_alias_matches_satellite_name(NonEmptyString rawBase, NonEmptyString rawAlias)
    {
        SatelliteCatalogMatching.ResetCache();

        var baseName = rawBase.Get.Replace("(", "").Replace(")", "");
        var alias = rawAlias.Get.Replace("(", "").Replace(")", "");

        if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(alias))
            return true; // skip degenerate cases

        // Enabled set has "BASENAME (ALIAS)"
        var enabledName = $"{baseName} ({alias})";
        var enabled = new HashSet<string>(new[] { enabledName }, StringComparer.OrdinalIgnoreCase);

        // Satellite named just "ALIAS" matches the alias inside the enabled name's brackets.
        var satellite = new SatelliteCatalogEntry
        {
            Name = alias,
            NoradId = "99999",
            Line1 = "1 99999U 00000A   24001.00000000  .00000000  00000-0  00000-0 0  0000",
            Line2 = "2 99999  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
        };

        var optimisedResult = SatelliteCatalogMatching.IsEnabled(satellite, enabled);
        var referenceResult = ReferenceIsEnabled(alias, enabled);

        return optimisedResult == referenceResult;
    }

    /// <summary>
    /// Property test: the optimised implementation preserves semantics when the same enabled
    /// set instance is reused across multiple calls (cache hit path).
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Cached_index_produces_same_results_across_calls(NonEmptyString rawSatName1, NonEmptyString rawSatName2, string[] rawEnabledNames)
    {
        SatelliteCatalogMatching.ResetCache();

        var enabledList = (rawEnabledNames ?? []).Where(n => n is not null).ToArray();
        var enabled = new HashSet<string>(enabledList, StringComparer.OrdinalIgnoreCase);

        var sat1 = new SatelliteCatalogEntry
        {
            Name = rawSatName1.Get,
            NoradId = "99999",
            Line1 = "1 99999U 00000A   24001.00000000  .00000000  00000-0  00000-0 0  0000",
            Line2 = "2 99999  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
        };
        var sat2 = new SatelliteCatalogEntry
        {
            Name = rawSatName2.Get,
            NoradId = "99998",
            Line1 = "1 99998U 00000A   24001.00000000  .00000000  00000-0  00000-0 0  0000",
            Line2 = "2 99998  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
        };

        // First call builds cache, second call uses cache
        var result1 = SatelliteCatalogMatching.IsEnabled(sat1, enabled);
        var result2 = SatelliteCatalogMatching.IsEnabled(sat2, enabled);

        var ref1 = ReferenceIsEnabled(rawSatName1.Get, enabled);
        var ref2 = ReferenceIsEnabled(rawSatName2.Get, enabled);

        return result1 == ref1 && result2 == ref2;
    }
}
