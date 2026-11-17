namespace OscarWatch.Core.Services;

public enum SatelliteDatabaseMergePresentation
{
    RemoteUpdate,
    FileImport
}

public static class SatelliteDatabaseMergePresentationExtensions
{
    public static string WindowTitle(this SatelliteDatabaseMergePresentation presentation) =>
        presentation switch
        {
            SatelliteDatabaseMergePresentation.FileImport => "Merge imported transponder database",
            _ => "Transponder database updates"
        };

    public static string SourceDescription(this SatelliteDatabaseMergePresentation presentation) =>
        presentation switch
        {
            SatelliteDatabaseMergePresentation.FileImport => "the imported file",
            _ => "oscarwatch.org"
        };

    public static string ConflictRemoteLabel(this SatelliteDatabaseMergePresentation presentation) =>
        presentation switch
        {
            SatelliteDatabaseMergePresentation.FileImport => "Use imported version",
            _ => "Use published version"
        };

    public static string IntroHint(this SatelliteDatabaseMergePresentation presentation) =>
        presentation switch
        {
            SatelliteDatabaseMergePresentation.FileImport =>
                "New entries are selected by default. Conflicts keep your editor copy unless you choose the imported version.",
            _ => "New entries are selected by default. Conflicts keep your local copy unless you choose the published version."
        };
}
