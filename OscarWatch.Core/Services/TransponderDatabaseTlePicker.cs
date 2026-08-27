using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public static class TransponderDatabaseTlePicker
{
    public static IReadOnlyList<SatelliteCatalogEntry> ListAvailable(
        IReadOnlyList<SatelliteCatalogEntry> catalog,
        IEnumerable<string> existingNames) =>
        ListAvailable(catalog, existingNames.Select(n => new SatelliteRadioEntry { Name = n }));

    public static IReadOnlyList<SatelliteCatalogEntry> ListAvailable(
        IReadOnlyList<SatelliteCatalogEntry> catalog,
        IEnumerable<SatelliteRadioEntry> existingEntries)
    {
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNoradIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in existingEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
                existingNames.Add(entry.Name.Trim());

            foreach (var alias in entry.AlternativeNames ?? [])
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    existingNames.Add(alias.Trim());
            }

            var noradId = CanonicalNoradId(entry.NoradId);
            if (noradId is not null)
                existingNoradIds.Add(noradId);
        }

        return catalog
            .Where(s =>
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                    return false;

                if (existingNames.Contains(s.Name.Trim()))
                    return false;

                var noradId = CanonicalNoradId(s.NoradId);
                return noradId is null || !existingNoradIds.Contains(noradId);
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? ResolveChosenName(string? selectedCatalogName, string customName)
    {
        var custom = customName.Trim();
        if (!string.IsNullOrEmpty(custom))
            return custom;

        var selected = selectedCatalogName?.Trim();
        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private static string? CanonicalNoradId(string? noradId) =>
        SatelliteDatabaseFile.NormalizeNoradId(noradId) is { } normalized
        && SatelliteDatabaseFile.IsValidNoradId(normalized)
            ? normalized
            : null;
}
