using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OscarWatch.Troubleshoot;

internal enum SettingsFileState
{
    Missing,
    Readable,
    Unreadable
}

/// <summary>A device switch in settings.json. <see cref="Enabled"/> is null when the section is absent.</summary>
internal sealed record DeviceState(bool? Enabled, string Target);

internal sealed record SettingsSnapshot(
    SettingsFileState State,
    DateTime? LastWriteLocal,
    string? Error,
    DeviceState Radio,
    DeviceState Rotator,
    DeviceState Gps);

internal enum DisableHardwareResult
{
    Disabled,
    AlreadyDisabled,
    Missing,
    Unreadable
}

/// <summary>
/// Reads and edits the tracker's settings.json as JSON nodes. The file is never round-tripped through a model,
/// so settings this build does not know about are written back unchanged.
/// </summary>
internal static class SettingsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly string[] HardwareSections = ["rig", "rotator", "gps"];

    private static readonly string[] SecretNameParts =
        ["password", "apikey", "apitoken", "token", "secret", "passcode"];

    private static readonly DeviceState UnknownDevice = new(null, "");

    public static string ReadUiLanguage(string path)
    {
        try
        {
            if (TryLoad(path, out var root, out _)
                && (GetChild(root, "uiLanguage") ?? GetChild(root, "UiLanguage")) is JsonValue value
                && value.TryGetValue<string>(out var language))
                return language;
        }
        catch
        {
            // use default
        }

        return "";
    }

    public static SettingsSnapshot Read(string path)
    {
        if (!File.Exists(path))
            return new SettingsSnapshot(SettingsFileState.Missing, null, null, UnknownDevice, UnknownDevice, UnknownDevice);

        DateTime? written = null;
        try
        {
            written = File.GetLastWriteTime(path);
        }
        catch
        {
            // leave unknown
        }

        if (!TryLoad(path, out var root, out var error))
            return new SettingsSnapshot(SettingsFileState.Unreadable, written, error, UnknownDevice, UnknownDevice, UnknownDevice);

        return new SettingsSnapshot(
            SettingsFileState.Readable,
            written,
            null,
            ReadRadio(GetChild(root, "rig") as JsonObject),
            ReadRotator(GetChild(root, "rotator") as JsonObject),
            ReadGps(GetChild(root, "gps") as JsonObject));
    }

    /// <summary>Sets <c>enabled</c> to false for the rig, rotator, and GPS. Everything else is left as it is.</summary>
    public static DisableHardwareResult DisableHardware(string path)
    {
        if (!File.Exists(path))
            return DisableHardwareResult.Missing;

        if (!TryLoad(path, out var root, out _))
            return DisableHardwareResult.Unreadable;

        var changed = false;
        foreach (var name in HardwareSections)
        {
            if (GetChild(root, name) is not JsonObject section)
                continue;

            if (ReadBool(section, "enabled") != true)
                continue;

            section["enabled"] = false;
            changed = true;
        }

        if (!changed)
            return DisableHardwareResult.AlreadyDisabled;

        BackUp(path);
        WriteAtomic(path, root.ToJsonString(WriteOptions));
        return DisableHardwareResult.Disabled;
    }

    /// <summary>
    /// Moves settings.json and every file the tracker would restore from (<c>.bak</c>, <c>.bak-*</c>,
    /// <c>.restored-from-bak-*</c>) into a dated folder. Leaving those beside settings.json would let the tracker
    /// bring the old settings straight back. Returns the folder, or null when there was nothing to move.
    /// </summary>
    public static string? PutAside(string path, DateTime utcNow)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;

        var files = EnumerateSettingsFiles(path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return null;

        var folder = Path.Combine(directory, "settings-aside-" + utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        if (Directory.Exists(folder))
            folder += "-" + Guid.NewGuid().ToString("N")[..8];

        Directory.CreateDirectory(folder);
        foreach (var file in files)
            File.Move(file, Path.Combine(folder, Path.GetFileName(file)));

        return folder;
    }

    /// <summary>Settings as indented JSON with passwords, API keys and tokens replaced by <c>***</c>.</summary>
    public static string Redacted(string path)
    {
        if (!File.Exists(path))
            return "(settings.json not found)";

        if (!TryLoad(path, out var root, out var error))
            return $"(settings.json cannot be read: {error})";

        RedactSecrets(root);
        return root.ToJsonString(WriteOptions);
    }

    internal static void RedactSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (IsSecretName(key) && obj[key] is JsonValue value
                        && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                        obj[key] = "***";
                    else
                        RedactSecrets(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    RedactSecrets(item);
                break;
        }
    }

    private static bool IsSecretName(string name) =>
        SecretNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateSettingsFiles(string path)
    {
        yield return path;
        yield return path + ".bak";
        yield return path + ".manual-restore-IO87jp";

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        foreach (var file in Directory.EnumerateFiles(directory, name + ".bak-*"))
            yield return file;
        foreach (var file in Directory.EnumerateFiles(directory, name + ".restored-from-bak-*"))
            yield return file;
    }

    private static DeviceState ReadRadio(JsonObject? rig)
    {
        if (rig is null)
            return UnknownDevice;

        string target;
        if (ReadBool(rig, "dualRadioEnabled") == true)
        {
            var parts = new[] { GetChild(rig, "downlink"), GetChild(rig, "uplink") }
                .OfType<JsonObject>()
                .Select(DescribeEndpoint)
                .Where(text => text.Length > 0);
            target = string.Join(" / ", parts);
        }
        else
        {
            target = DescribeEndpoint(rig);
        }

        return new DeviceState(ReadBool(rig, "enabled"), target);
    }

    private static DeviceState ReadRotator(JsonObject? rotator)
    {
        if (rotator is null)
            return UnknownDevice;

        var type = ReadString(rotator, "type");
        var transport = ReadString(rotator, "transportKind");
        var alwaysSerial = string.Equals(type, "greenHeronRt21", StringComparison.OrdinalIgnoreCase);
        var alwaysNetwork = string.Equals(type, "urcTcp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "spidMd01", StringComparison.OrdinalIgnoreCase);
        var serialTransport = transport.Length == 0 || transport == "0"
            || string.Equals(transport, "serial", StringComparison.OrdinalIgnoreCase);

        string target;
        if (alwaysNetwork || (!alwaysSerial && !serialTransport))
        {
            target = HostAndPort(ReadString(rotator, "networkHost"), ReadString(rotator, "networkPort"));
        }
        else
        {
            var ports = new[] { ReadString(rotator, "port"), ReadString(rotator, "elevationPort") }
                .Where(port => port.Length > 0);
            target = string.Join(" + ", ports);
        }

        return new DeviceState(ReadBool(rotator, "enabled"), target);
    }

    private static DeviceState ReadGps(JsonObject? gps)
    {
        if (gps is null)
            return UnknownDevice;

        var kind = ReadString(gps, "connectionKind");
        var usesGpsd = kind == "1" || string.Equals(kind, "gpsd", StringComparison.OrdinalIgnoreCase);
        var target = usesGpsd
            ? "gpsd " + HostAndPort(ReadString(gps, "gpsdHost"), ReadString(gps, "gpsdPort"))
            : ReadString(gps, "port");
        return new DeviceState(ReadBool(gps, "enabled"), target.Trim());
    }

    private static string DescribeEndpoint(JsonObject endpoint)
    {
        var port = ReadString(endpoint, "port");
        return port.Length > 0
            ? port
            : HostAndPort(ReadString(endpoint, "networkHost"), ReadString(endpoint, "networkPort"));
    }

    private static string HostAndPort(string host, string port)
    {
        if (host.Length == 0)
            return "";
        return port.Length == 0 ? host : $"{host}:{port}";
    }

    private static bool? ReadBool(JsonObject obj, string name) =>
        GetChild(obj, name) is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static string ReadString(JsonObject obj, string name)
    {
        if (GetChild(obj, name) is not JsonValue value)
            return "";

        if (value.TryGetValue<string>(out var text))
            return text.Trim();

        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : "";
    }

    private static JsonNode? GetChild(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) ? node : null;

    private static bool TryLoad(string path, out JsonObject root, out string? error)
    {
        root = new JsonObject();
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) is not JsonObject parsed)
            {
                error = "not a JSON object";
                return false;
            }

            root = parsed;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void BackUp(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = path + ".bak-" + stamp;
        if (File.Exists(backup))
            backup += "-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(path, backup, overwrite: false);
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
