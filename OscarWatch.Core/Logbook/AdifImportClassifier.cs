using OscarWatch.Core.Geo;

namespace OscarWatch.Core.Logbook;

public enum AdifImportDuplicateSource
{
    Logbook,
    EarlierInFile
}

public sealed class AdifImportCandidate
{
    public required AdifImportedQso Qso { get; init; }
    public bool IsDuplicate { get; init; }
    public AdifImportDuplicateSource? DuplicateSource { get; init; }
}

/// <summary>Counts and lists after applying the satellite-only filter and duplicate matching.</summary>
public sealed class AdifImportPreview
{
    public required IReadOnlyList<AdifImportCandidate> NewQsos { get; init; }
    public required IReadOnlyList<AdifImportCandidate> Duplicates { get; init; }
    public int UnparsedCount { get; init; }
    public int FilteredOutCount { get; init; }

    public int NewCount => NewQsos.Count;
    public int DuplicateCount => Duplicates.Count;
}

/// <summary>
/// Classifies parsed ADI records against existing logbook keys and earlier rows in the same file.
/// Duplicate key: normalised callsign + UTC date and time to the minute.
/// </summary>
public static class AdifImportClassifier
{
    public static AdifImportPreview BuildPreview(
        AdifImportParseResult parsed,
        IReadOnlySet<string> existingDuplicateKeys,
        bool satelliteOnly)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(existingDuplicateKeys);

        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var neu = new List<AdifImportCandidate>();
        var dupes = new List<AdifImportCandidate>();
        var filteredOut = 0;

        foreach (var qso in parsed.Records)
        {
            if (satelliteOnly && !qso.IsSatelliteContact)
            {
                filteredOut++;
                continue;
            }

            var key = AdifImporter.FormatDuplicateKey(qso.Call, qso.QsoUtc);
            if (existingDuplicateKeys.Contains(key))
            {
                dupes.Add(new AdifImportCandidate
                {
                    Qso = qso,
                    IsDuplicate = true,
                    DuplicateSource = AdifImportDuplicateSource.Logbook
                });
                continue;
            }

            if (!seenInFile.Add(key))
            {
                dupes.Add(new AdifImportCandidate
                {
                    Qso = qso,
                    IsDuplicate = true,
                    DuplicateSource = AdifImportDuplicateSource.EarlierInFile
                });
                continue;
            }

            neu.Add(new AdifImportCandidate
            {
                Qso = qso,
                IsDuplicate = false
            });
        }

        return new AdifImportPreview
        {
            NewQsos = neu,
            Duplicates = dupes,
            UnparsedCount = parsed.UnparsedCount,
            FilteredOutCount = filteredOut
        };
    }

    public static HashSet<string> BuildDuplicateKeySet(
        IEnumerable<(string Call, DateTime QsoUtc)> existing)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (call, qsoUtc) in existing)
        {
            var normalised = MaidenheadLocator.NormalizeCallsign(call);
            if (normalised.Length == 0)
                continue;
            set.Add(AdifImporter.FormatDuplicateKey(normalised, qsoUtc));
        }

        return set;
    }
}
