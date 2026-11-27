using System.Text.Json.Serialization;

namespace OscarWatch.Core.Models;

public sealed class SatelliteRadioEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("norad_id")]
    public string? NoradId { get; set; }

    [JsonPropertyName("downlink_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DownlinkMinKHz { get; set; }

    [JsonPropertyName("downlink_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DownlinkMaxKHz { get; set; }

    [JsonPropertyName("uplink_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UplinkMinKHz { get; set; }

    [JsonPropertyName("uplink_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UplinkMaxKHz { get; set; }

    [JsonIgnore]
    public bool HasDownlinkPassband =>
        DownlinkMinKHz is > 0 && DownlinkMaxKHz is > 0 && DownlinkMaxKHz > DownlinkMinKHz;

    [JsonIgnore]
    public bool HasUplinkPassband =>
        UplinkMinKHz is > 0 && UplinkMaxKHz is > 0 && UplinkMaxKHz > UplinkMinKHz;

    /// <summary>
    /// Extra names used for TLE / published-database matching when the preferred
    /// <see cref="Name"/> differs (for example after a rename, or nicknames such as RS40-S).
    /// </summary>
    [JsonPropertyName("alternative_names")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AlternativeNames { get; set; }

    /// <summary>
    /// Optional operator notices (recurring schedules, temporary mode restrictions, and similar).
    /// Synced from the published database when present.
    /// </summary>
    [JsonPropertyName("notices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SatelliteOperatingNotice>? Notices { get; set; }

    [JsonPropertyName("modes")]
    public List<SatelliteTransponderMode> Modes { get; set; } = [];
}
