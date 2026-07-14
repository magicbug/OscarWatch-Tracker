using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// JSON serialisation and deserialisation for session plans.
/// </summary>
public static class SessionPlanPersistence
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Serialises a session plan to a JSON string.
    /// </summary>
    public static string Serialise(SessionPlan plan)
    {
        var dto = new SessionPlanDto
        {
            Version = CurrentVersion,
            SessionStartUtc = plan.SessionStartUtc,
            SessionEndUtc = plan.SessionEndUtc,
            GeneratedUtc = DateTime.UtcNow,
            Parameters = new PlanParametersDto(),
            ScheduledPasses = plan.ScheduledPasses.Select(sp => new ScheduledPassDto
            {
                SatelliteName = sp.Scored.Pass.SatelliteName,
                NoradId = sp.Scored.Pass.NoradId,
                AosUtc = sp.Scored.Pass.AosUtc,
                LosUtc = sp.Scored.Pass.LosUtc,
                MaxElevationDeg = sp.Scored.Pass.MaxElevationDeg,
                MaxElevationUtc = sp.Scored.Pass.MaxElevationUtc,
                QualityScore = sp.Scored.QualityScore,
                CompositeScore = sp.Scored.CompositeScore,
                SatellitePriority = sp.Scored.SatellitePriority,
                Reason = sp.Reason
            }).ToList(),
            ExcludedIds = plan.ExcludedIds.ToList(),
            ForcedInclusionIds = plan.ForcedInclusionIds.ToList()
        };

        return JsonSerializer.Serialize(dto, SerialiserOptions);
    }

    /// <summary>
    /// Deserialises a JSON string to a session plan.
    /// Returns null for malformed JSON or version mismatch.
    /// </summary>
    public static SessionPlan? Deserialise(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<SessionPlanDto>(json, SerialiserOptions);

            if (dto is null || dto.Version != CurrentVersion)
                return null;

            var scheduledPasses = dto.ScheduledPasses.Select(sp =>
            {
                var passInfo = new PassInfo
                {
                    SatelliteName = sp.SatelliteName,
                    NoradId = sp.NoradId,
                    AosUtc = sp.AosUtc,
                    LosUtc = sp.LosUtc,
                    MaxElevationDeg = sp.MaxElevationDeg,
                    MaxElevationUtc = sp.MaxElevationUtc
                };

                var scored = new ScoredPass
                {
                    Pass = passInfo,
                    QualityScore = sp.QualityScore,
                    CompositeScore = sp.CompositeScore,
                    SatellitePriority = sp.SatellitePriority
                };

                return new ScheduledPass
                {
                    Scored = scored,
                    Reason = sp.Reason
                };
            }).ToList();

            // On deserialise, AllCandidates is reconstructed from the scheduled passes.
            // The original full candidate list isn't preserved across save/load — only
            // scheduled passes are stored. This means a loaded plan cannot be meaningfully
            // "re-solved" without re-running prediction, but all scheduled data is intact.
            var allCandidates = scheduledPasses
                .Select(sp => sp.Scored)
                .ToList();

            return new SessionPlan
            {
                SessionStartUtc = dto.SessionStartUtc,
                SessionEndUtc = dto.SessionEndUtc,
                ScheduledPasses = scheduledPasses,
                AllCandidates = allCandidates,
                ExcludedIds = dto.ExcludedIds.ToHashSet(),
                ForcedInclusionIds = dto.ForcedInclusionIds.ToHashSet()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #region Internal DTOs

    internal sealed class SessionPlanDto
    {
        public int Version { get; set; }
        public DateTime SessionStartUtc { get; set; }
        public DateTime SessionEndUtc { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public PlanParametersDto Parameters { get; set; } = new();
        public List<ScheduledPassDto> ScheduledPasses { get; set; } = [];
        public List<string> ExcludedIds { get; set; } = [];
        public List<string> ForcedInclusionIds { get; set; } = [];
    }

    internal sealed class PlanParametersDto
    {
        public double MinimumElevationDeg { get; set; } = 5.0;
        public int PreAlertMinutes { get; set; } = 3;
        public Dictionary<string, int> Priorities { get; set; } = new();
    }

    internal sealed class ScheduledPassDto
    {
        public string SatelliteName { get; set; } = string.Empty;
        public string NoradId { get; set; } = string.Empty;
        public DateTime AosUtc { get; set; }
        public DateTime LosUtc { get; set; }
        public double MaxElevationDeg { get; set; }
        public DateTime MaxElevationUtc { get; set; }
        public double QualityScore { get; set; }
        public double CompositeScore { get; set; }
        public int SatellitePriority { get; set; }
        public PassSelectionReason Reason { get; set; }
    }

    #endregion
}
