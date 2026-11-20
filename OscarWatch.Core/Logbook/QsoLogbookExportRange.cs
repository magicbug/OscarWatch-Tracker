namespace OscarWatch.Core.Logbook;

public static class QsoLogbookExportRange
{
    public static (DateTime FromUtcDate, DateTime ToUtcDate) NormalizeUtcDates(DateTime fromUtcDate, DateTime toUtcDate)
    {
        var from = QsoLogbookTime.NormalizeToUtc(fromUtcDate).Date;
        var to = QsoLogbookTime.NormalizeToUtc(toUtcDate).Date;
        return from <= to ? (from, to) : (to, from);
    }

    public static (DateTime? FromInclusive, DateTime? ToExclusive) ToQueryBounds(QsoAdifExportOptions options)
    {
        if (options.Scope != QsoAdifExportScope.DateRange)
            return (null, null);

        var (fromDate, toDate) = NormalizeUtcDates(options.FromUtcDate, options.ToUtcDate);
        return (fromDate, toDate.AddDays(1));
    }

    public static (DateTime FromUtcDate, DateTime ToUtcDate) DefaultUtcDates(IReadOnlyList<DateTime> qsoUtcValues)
    {
        var today = DateTime.UtcNow.Date;
        if (qsoUtcValues.Count == 0)
            return (today, today);

        var latest = qsoUtcValues.Max(QsoLogbookTime.NormalizeToUtc).Date;
        return (latest, latest);
    }
}
