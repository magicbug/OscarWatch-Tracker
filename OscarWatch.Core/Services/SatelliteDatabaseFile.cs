using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Json;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Tle;

namespace OscarWatch.Core.Services;

public static class SatelliteDatabaseFile
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() =>
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new FlexibleDoubleJsonConverter() }
        };

    public static List<SatelliteRadioEntry> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        return ParseJson(json);
    }

    public static List<SatelliteRadioEntry> ParseJson(string json) =>
        JsonSerializer.Deserialize<List<SatelliteRadioEntry>>(json, Options) ?? [];

    public static void Save(string path, IEnumerable<SatelliteRadioEntry> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, SerializeEntries(entries));
    }

    public static string SerializeEntries(IEnumerable<SatelliteRadioEntry> entries)
    {
        var list = entries
            .Select(NormalizeEntry)
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(list, Options);
    }

    public static void CopyBundledToUser(string bundledPath, string userPath)
    {
        if (!File.Exists(bundledPath))
            throw new FileNotFoundException("Bundled satellite database not found.", bundledPath);

        var directory = Path.GetDirectoryName(userPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.Copy(bundledPath, userPath, overwrite: true);
    }

    public static SatelliteRadioEntry NormalizeEntry(SatelliteRadioEntry entry)
    {
        entry.Name = entry.Name.Trim();
        entry.NoradId = NormalizeNoradId(entry.NoradId);
        entry.AlternativeNames = NormalizeAlternativeNames(entry.Name, entry.AlternativeNames);
        if (entry.AlternativeNames is { Count: 0 })
            entry.AlternativeNames = null;
        entry.Modes = entry.Modes
            .Where(m => !string.IsNullOrWhiteSpace(m.Type) || m.DownlinkKHz > 0 || m.UplinkKHz > 0)
            .ToList();

        foreach (var mode in entry.Modes)
        {
            mode.Type = mode.Type.Trim();
            mode.DownlinkMode = TransponderCatModes.Normalize(mode.DownlinkMode);
            mode.UplinkMode = TransponderCatModes.Normalize(mode.UplinkMode);
            mode.Doppler = string.IsNullOrWhiteSpace(mode.Doppler) ? "NOR" : mode.Doppler.Trim().ToUpperInvariant();
            if (mode.CtcssHz is <= 0)
                mode.CtcssHz = null;
            if (mode.CtcssArmHz is <= 0)
                mode.CtcssArmHz = null;
        }

        return entry;
    }

    public static List<string> NormalizeAlternativeNames(string name, IEnumerable<string>? alternativeNames)
    {
        if (alternativeNames is null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in alternativeNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, name.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(trimmed))
                continue;

            result.Add(trimmed);
        }

        return result;
    }

    public static string? ValidateEntries(IReadOnlyList<SatelliteRadioEntry> entries)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                return "Every satellite needs a name.";

            var name = entry.Name.Trim();
            if (!seenNames.Add(name))
                return $"Duplicate satellite name: {entry.Name}";

            if (entry.Modes.Count == 0)
                return $"{entry.Name} has no transponder modes.";

            if (!string.IsNullOrWhiteSpace(entry.NoradId) && !IsValidNoradId(entry.NoradId))
                return $"{entry.Name}: NORAD ID must be a catalogue number (0–339999) or Alpha-5 field (e.g. A0000).";

            foreach (var alias in entry.AlternativeNames ?? [])
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                var trimmedAlias = alias.Trim();
                if (string.Equals(trimmedAlias, name, StringComparison.OrdinalIgnoreCase))
                    return $"{entry.Name}: alternative name cannot match the preferred name.";

                if (!seenNames.Add(trimmedAlias))
                    return $"Duplicate satellite name or alternative name: {trimmedAlias}";
            }
        }

        return null;
    }

    internal static string? NormalizeNoradId(string? noradId)
    {
        if (string.IsNullOrWhiteSpace(noradId))
            return null;

        return Alpha5CatalogId.Normalize(noradId.Trim()) ?? noradId.Trim();
    }

    internal static bool IsValidNoradId(string noradId) =>
        Alpha5CatalogId.IsSupportedCatalogueId(noradId);
}
