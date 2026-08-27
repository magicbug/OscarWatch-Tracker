using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Resolves the preferred operator-facing satellite label from the transponder database.
/// </summary>
public static class SatelliteDisplayName
{
    public static string Resolve(
        string catalogName,
        string? noradId,
        ISatelliteDatabaseService? database)
    {
        if (database is null)
            return catalogName;

        var entry = database.TryGetEntry(catalogName, noradId);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
            return catalogName;

        return entry.Name.Trim();
    }
}
