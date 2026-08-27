using System.Text.Json.Serialization;

namespace OscarWatch.Core.Models;

public sealed class SatelliteRadioEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("norad_id")]
    public string? NoradId { get; set; }

    /// <summary>
    /// Extra names used for TLE / published-database matching when the preferred
    /// <see cref="Name"/> differs (for example after a rename, or nicknames such as RS40-S).
    /// </summary>
    [JsonPropertyName("alternative_names")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AlternativeNames { get; set; }

    [JsonPropertyName("modes")]
    public List<SatelliteTransponderMode> Modes { get; set; } = [];
}
