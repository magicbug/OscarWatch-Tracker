namespace OscarWatch.Core.Models;

public sealed class PassScheduleSettings
{
    public const int DefaultLeadMinutesBeforeAos = 5;
    public const int MinLeadMinutesBeforeAos = 1;
    public const int MaxLeadMinutesBeforeAos = 60;

    /// <summary>Minutes before AOS to fire the scheduled-pass ding and alert.</summary>
    public int LeadMinutesBeforeAos { get; set; } = DefaultLeadMinutesBeforeAos;

    public bool SoundEnabled { get; set; } = true;

    public bool AlertEnabled { get; set; } = true;

    public static int ClampLeadMinutes(int minutes) =>
        Math.Clamp(minutes, MinLeadMinutesBeforeAos, MaxLeadMinutesBeforeAos);
}
