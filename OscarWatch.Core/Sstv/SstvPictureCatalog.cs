namespace OscarWatch.Core.Sstv;

/// <summary>A saved SSTV picture, described from its file name.</summary>
/// <param name="ReceivedUtc">From the file name, or the file time when the name is not ours.</param>
/// <param name="Satellite">Satellite (or recording) name from the file name, if any.</param>
/// <param name="Mode">Mode from the file name, or null when the name is not ours.</param>
public sealed record SstvStoredPicture(string Path, DateTime ReceivedUtc, string? Satellite, SstvMode? Mode, long Bytes);

/// <summary>Lists the pictures in the SSTV folder, reading the names that <see cref="SstvPaths.PictureStem"/> writes.</summary>
public static class SstvPictureCatalog
{
    /// <summary>Every PNG in <paramref name="directory"/>, newest first. Empty when the folder does not exist.</summary>
    public static IReadOnlyList<SstvStoredPicture> Scan(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var list = new List<SstvStoredPicture>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            try
            {
                var info = new FileInfo(path);
                list.Add(Describe(path, info.LastWriteTimeUtc, info.Length));
            }
            catch (IOException)
            {
                // Deleted or locked while listing.
            }
        }

        list.Sort((a, b) => b.ReceivedUtc.CompareTo(a.ReceivedUtc));
        return list;
    }

    /// <summary>
    /// Reads <c>yyyyMMdd_HHmmssZ[_satellite]_Mode[_n].png</c>. Any other name falls back to
    /// <paramref name="fileTimeUtc"/> with no satellite or mode.
    /// </summary>
    public static SstvStoredPicture Describe(string path, DateTime fileTimeUtc, long bytes)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var parts = stem.Split('_');
        if (parts.Length >= 3
            && parts[1].EndsWith('Z')
            && DateTime.TryParseExact(
                parts[0] + parts[1][..^1],
                "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var utc))
        {
            var rest = parts[2..].ToList();
            // A trailing number is the "_2" added when a name was already taken.
            if (rest.Count >= 2 && rest[^1].All(char.IsAsciiDigit) && ModeFromToken(rest[^2]) is not null)
                rest.RemoveAt(rest.Count - 1);

            if (ModeFromToken(rest[^1]) is { } mode)
            {
                var satellite = rest.Count > 1 ? string.Join('_', rest.Take(rest.Count - 1)) : null;
                return new SstvStoredPicture(path, DateTime.SpecifyKind(utc, DateTimeKind.Utc), satellite, mode, bytes);
            }
        }

        return new SstvStoredPicture(path, DateTime.SpecifyKind(fileTimeUtc, DateTimeKind.Utc), null, null, bytes);
    }

    private static SstvMode? ModeFromToken(string token)
    {
        foreach (var mode in SstvModeTable.All)
        {
            if (string.Equals(mode.Id.ToString(), token, StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        return null;
    }
}
