using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// Edge-case tests for <see cref="SatelliteCatalogMatching"/>: bracketed aliases,
/// catalogue ID preference, Alpha-5 normalisation, and additive ID migrate.
/// </summary>
public sealed class SatelliteCatalogMatchingTests
{
    [Fact]
    public void Enabled_set_with_whitespace_only_entries_does_not_match()
    {
        var satellite = Entry("ISS (ZARYA)", "25544");
        var enabledSet = new HashSet<string> { "   ", "  \t  ", " " };

        Assert.False(SatelliteCatalogMatching.IsEnabled(satellite, enabledSet));
    }

    [Fact]
    public void OrigamiSat_2_does_not_enable_ISAT_via_mid_token_substring()
    {
        var isat = Entry("ISAT", "43879");
        var origami = Entry("OrigamiSat 2", "68795");
        var enabled = new HashSet<string>(["OrigamiSat 2"], StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(origami, enabled));
        Assert.False(SatelliteCatalogMatching.IsEnabled(isat, enabled));
    }

    [Fact]
    public void Parenthetical_alias_still_matches_SO_50()
    {
        var so50 = Entry("SAUDISAT 1C (SO-50)", "27607");
        var enabled = new HashSet<string>(["SO-50"], StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(so50, enabled));
    }

    [Fact]
    public void Parenthetical_prefix_matches_ISS_and_alias_matches_ZARYA()
    {
        var issLong = Entry("ISS (ZARYA)", "25544");
        var iss = Entry("ISS", "25544");
        var zarya = Entry("ZARYA", "25544");

        var byShort = new HashSet<string>(["ISS"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(issLong, byShort));
        Assert.False(SatelliteCatalogMatching.IsEnabled(zarya, byShort));

        var byLong = new HashSet<string>(["ISS (ZARYA)"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(iss, byLong));
        Assert.True(SatelliteCatalogMatching.IsEnabled(zarya, byLong));
    }

    [Fact]
    public void Meteor_family_names_do_not_cross_enable()
    {
        var shortName = Entry("METEOR-M2", "40069");
        var meteorM2 = Entry("METEOR-M 2", "40069");
        var m2 = Entry("METEOR-M2 2", "44387");
        var m3 = Entry("METEOR-M2 3", "57166");
        var m4 = Entry("METEOR-M2 4", "59051");
        var n2 = Entry("METEOR-M N2", "40069");
        var n2Dash3 = Entry("METEOR-M N2-3", "57166");

        var oneNumbered = new HashSet<string>(["METEOR-M2 3"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(m3, oneNumbered));
        Assert.False(SatelliteCatalogMatching.IsEnabled(shortName, oneNumbered));
        Assert.False(SatelliteCatalogMatching.IsEnabled(meteorM2, oneNumbered));
        Assert.False(SatelliteCatalogMatching.IsEnabled(m2, oneNumbered));
        Assert.False(SatelliteCatalogMatching.IsEnabled(m4, oneNumbered));

        var shortOnly = new HashSet<string>(["METEOR-M2"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(shortName, shortOnly));
        Assert.False(SatelliteCatalogMatching.IsEnabled(m2, shortOnly));
        Assert.False(SatelliteCatalogMatching.IsEnabled(m3, shortOnly));
        Assert.False(SatelliteCatalogMatching.IsEnabled(m4, shortOnly));

        var hyphen = new HashSet<string>(["METEOR-M N2"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(n2, hyphen));
        Assert.False(SatelliteCatalogMatching.IsEnabled(n2Dash3, hyphen));
    }

    [Fact]
    public void Noaa_parenthetical_name_does_not_select_the_other_noaa_bird()
    {
        var noaa20 = Entry("NOAA 20 (JPSS-1)", "43013");
        var noaa21 = Entry("NOAA 21 (JPSS-2)", "54234");

        var byLong = new HashSet<string>(["NOAA 20 (JPSS-1)"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(noaa20, byLong));
        Assert.False(SatelliteCatalogMatching.IsEnabled(noaa21, byLong));

        var byAlias = new HashSet<string>(["JPSS-1"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(noaa20, byAlias));
        Assert.False(SatelliteCatalogMatching.IsEnabled(noaa21, byAlias));

        var byPrefix = new HashSet<string>(["NOAA 20"], StringComparer.OrdinalIgnoreCase);
        Assert.True(SatelliteCatalogMatching.IsEnabled(noaa20, byPrefix));
        Assert.False(SatelliteCatalogMatching.IsEnabled(noaa21, byPrefix));
    }

    [Fact]
    public void Id_match_enables_when_catalog_name_differs()
    {
        var origami = Entry("OrigamiSat 2", "68795");
        var ids = SatelliteCatalogMatching.CreateNoradIdSet(["68795"]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(origami, ids, names));
    }

    [Fact]
    public void Name_fallback_used_when_ids_empty()
    {
        var origami = Entry("OrigamiSat 2", "68795");
        var ids = SatelliteCatalogMatching.CreateNoradIdSet([]);
        var names = new HashSet<string>(["OrigamiSat 2"], StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(origami, ids, names));
    }

    [Fact]
    public void OrigamiSat_id_does_not_enable_ISAT()
    {
        var isat = Entry("ISAT", "43879");
        var ids = SatelliteCatalogMatching.CreateNoradIdSet(["68795"]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(SatelliteCatalogMatching.IsEnabled(isat, ids, names));
    }

    [Fact]
    public void Numeric_100000_matches_catalog_A0000()
    {
        var sat = Entry("HIGH-CAT", "A0000");
        var ids = SatelliteCatalogMatching.CreateNoradIdSet(["100000"]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(sat, ids, names));
        Assert.Equal("A0000", SatelliteCatalogMatching.NormalizeNoradId("100000"));
    }

    [Fact]
    public void TryMigrate_appends_ids_from_names_without_removing_names()
    {
        var so50 = Entry("SAUDISAT 1C (SO-50)", "27607");
        var origami = Entry("OrigamiSat 2", "68795");
        var settings = new AppSettings
        {
            EnabledSatelliteNames = ["SO-50", "OrigamiSat 2"],
            EnabledSatelliteNoradIds = []
        };

        Assert.True(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(settings, [so50, origami]));

        Assert.Contains("SO-50", settings.EnabledSatelliteNames);
        Assert.Contains("OrigamiSat 2", settings.EnabledSatelliteNames);
        Assert.Contains("SAUDISAT 1C (SO-50)", settings.EnabledSatelliteNames);
        Assert.Equal(["27607", "68795"], settings.EnabledSatelliteNoradIds.OrderBy(id => id).ToList());
    }

    [Fact]
    public void TryMigrate_normalises_existing_ids_and_is_idempotent()
    {
        var sat = Entry("HIGH-CAT", "A0000");
        var settings = new AppSettings
        {
            EnabledSatelliteNames = ["HIGH-CAT"],
            EnabledSatelliteNoradIds = ["100000"]
        };

        Assert.True(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(settings, [sat]));
        Assert.Equal(["A0000"], settings.EnabledSatelliteNoradIds);
        Assert.Equal(["HIGH-CAT"], settings.EnabledSatelliteNames);

        Assert.False(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(settings, [sat]));
    }

    [Fact]
    public void TryMigrate_adds_current_catalog_name_spelling_when_missing()
    {
        var origami = Entry("OrigamiSat 2", "68795");
        var settings = new AppSettings
        {
            EnabledSatelliteNames = ["ORIGAMISAT-2"],
            EnabledSatelliteNoradIds = []
        };

        // Token match won't link ORIGAMISAT-2 to OrigamiSat 2 (hyphen vs space).
        // Exact/token: "OrigamiSat 2" vs "ORIGAMISAT-2" — no match. Migrate won't add.
        Assert.False(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(settings, [origami]));

        // With a name that does match:
        settings.EnabledSatelliteNames = ["OrigamiSat 2"];
        Assert.True(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(settings, [origami]));
        Assert.Contains("68795", settings.EnabledSatelliteNoradIds);
    }

    [Fact]
    public void TryMigrate_does_not_append_sibling_meteor_names()
    {
        var shortName = Entry("METEOR-M2", "40069");
        var m2 = Entry("METEOR-M2 2", "44387");
        var m3 = Entry("METEOR-M2 3", "57166");
        var m4 = Entry("METEOR-M2 4", "59051");
        var settings = new AppSettings
        {
            EnabledSatelliteNames = ["METEOR-M2 3"],
            EnabledSatelliteNoradIds = []
        };

        Assert.True(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(
            settings, [shortName, m2, m3, m4]));

        Assert.Equal(["METEOR-M2 3"], settings.EnabledSatelliteNames);
        Assert.Equal(["57166"], settings.EnabledSatelliteNoradIds);

        Assert.False(SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(
            settings, [shortName, m2, m3, m4]));
    }

    private static SatelliteCatalogEntry Entry(string name, string noradId) => new()
    {
        Name = name,
        NoradId = noradId,
        Line1 = $"1 {noradId}U 00000A   00001.00000000  .00000000  00000-0  00000-0 0  0000",
        Line2 = $"2 {noradId}  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000"
    };
}
