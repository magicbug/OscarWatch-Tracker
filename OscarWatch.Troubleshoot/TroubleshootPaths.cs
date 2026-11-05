namespace OscarWatch.Troubleshoot;

/// <summary>Same locations the tracker uses under <see cref="Environment.SpecialFolder.ApplicationData"/>.</summary>
internal static class TroubleshootPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OscarWatch");

    public static string LogFolder => Path.Combine(DataFolder, "logs");

    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");
}
