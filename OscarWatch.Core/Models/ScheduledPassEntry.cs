namespace OscarWatch.Core.Models;

/// <summary>A specific upcoming pass the operator has scheduled for a pre-AOS reminder.</summary>
public sealed class ScheduledPassEntry
{
    public string NoradId { get; set; } = "";

    public DateTime AosUtc { get; set; }
}
