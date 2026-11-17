namespace OscarWatch.Core.Services;

public static class SatelliteDatabasePaths
{
    /// <summary>Published transponder database (merge sync planned; see documents/satellite-database.md).</summary>
    public const string RemoteDatabaseUrl = "https://oscarwatch.org/satellite_database.json";

    public static string UserDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OscarWatch",
        "satellite_database.json");

    public static string BundledDatabasePath(string appBaseDirectory) =>
        Path.Combine(appBaseDirectory, "Assets", "satellite_database.json");
}
