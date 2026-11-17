namespace OscarWatch.Core.Logbook;

public enum QsoAdifExportScope
{
    All,
    DateRange,
    Selected
}

public sealed class QsoAdifExportOptions
{
    public QsoAdifExportScope Scope { get; init; } = QsoAdifExportScope.All;

    /// <summary>UTC calendar date (inclusive) when <see cref="Scope"/> is <see cref="QsoAdifExportScope.DateRange"/>.</summary>
    public DateTime FromUtcDate { get; init; }

    /// <summary>UTC calendar date (inclusive) when <see cref="Scope"/> is <see cref="QsoAdifExportScope.DateRange"/>.</summary>
    public DateTime ToUtcDate { get; init; }

    public bool ForLotw { get; init; }
}

public sealed record QsoAdifExportDialogDefaults(
    DateTimeOffset FromUtcDate,
    DateTimeOffset ToUtcDate,
    int SelectedCount = 0,
    QsoAdifExportScope InitialScope = QsoAdifExportScope.All);
