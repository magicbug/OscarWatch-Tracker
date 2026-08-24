namespace OscarWatch.Core.Services;

/// <summary>Plays a short system alert sound for scheduled-pass reminders.</summary>
public interface IAlertSoundService
{
    /// <summary>Best-effort ding; never throws. May be a no-op when playback is unavailable.</summary>
    void PlayAlert();
}
