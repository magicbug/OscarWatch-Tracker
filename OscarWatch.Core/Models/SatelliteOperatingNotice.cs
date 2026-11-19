using System.Text.Json.Serialization;

namespace OscarWatch.Core.Models;

/// <summary>
/// Operator-facing notice for a satellite (for example a recurring digital-modes schedule).
/// Stored in <c>satellite_database.json</c> so announcements can sync without an app release.
/// </summary>
public sealed class SatelliteOperatingNotice
{
    /// <summary>Operator-facing notice copy (English in the published database).</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>
    /// Optional UTC weekday filter (for example <c>["Wednesday"]</c>).
    /// Omitted or empty means the notice applies every day.
    /// </summary>
    [JsonPropertyName("days_of_week_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DaysOfWeekUtc { get; set; }

    /// <summary>
    /// Optional mode-type filter matching <see cref="SatelliteTransponderMode.Type"/>
    /// (for example <c>["FM Repeater"]</c>). Omitted or empty means the whole satellite.
    /// </summary>
    [JsonPropertyName("mode_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ModeTypes { get; set; }
}
