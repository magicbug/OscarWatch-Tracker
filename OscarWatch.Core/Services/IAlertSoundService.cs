namespace OscarWatch.Core.Services;

/// <summary>Plays short alert sounds through the computer speakers.</summary>
public interface IAlertSoundService
{
    /// <summary>Scheduled-pass ding. Best-effort; never throws.</summary>
    void PlayAlert();

    /// <summary>FT4 call alert. A different tone from <see cref="PlayAlert"/>. Best-effort; never throws.</summary>
    void PlayCall();
}
