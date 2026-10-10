using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public interface IRigController
{
    RigConnectionStatus GetStatus();

    /// <summary>Enqueue latest pass/settings for the dedicated rig thread (~1–4 Hz from UI).</summary>
    /// <param name="reinitializePass">When true and the pass key is unchanged, re-run SAT mode / frequency setup (e.g. user re-selected a satellite). When false, offset-only updates force an immediate doppler write.</param>
    /// <param name="catPausedOverride">When non-null, overrides <see cref="RigSettings.CatUpdatesPaused"/> without cloning the settings object.</param>
    void PublishContext(RigSettings settings, RigTrackingContext? context, bool reinitializePass = false, bool? catPausedOverride = null);

    /// <summary>Synchronous publish + doppler tick on the rig thread (unit tests).</summary>
    void Update(RigSettings settings, RigTrackingContext? context);

    /// <summary>User changed the CTCSS selector — always program uplink Sub/VFO B (even if CAT paused).</summary>
    void ApplySelectedCtcss(RigSettings settings, RigTrackingContext? context);

    /// <summary>Key or unkey via CAT on the uplink (or single) radio.</summary>
    void SetPtt(bool transmit);

    /// <summary>Assert or release RTS/DTR on the CAT serial port.</summary>
    void SetHandshakePtt(bool useRts, bool assert);

    /// <summary>
    /// When true, FT4 slot-gates CAT Doppler: the dial is held between forced steps
    /// (OrbitDeck <c>holdDoppler</c>). When false, continuous Doppler resumes.
    /// </summary>
    void SetFt4SlotGatedDoppler(bool hold);

    /// <summary>Apply one Doppler write now while FT4 slot-gating is active (slot boundary).</summary>
    void ForceFt4DopplerStep();

    /// <summary>
    /// Apply one FT4 Doppler write aimed at <paramref name="frequencyAtUtc"/> and wait
    /// up to <paramref name="timeout"/> for the rig thread to finish it.
    /// Returns false when it did not finish in time. The write may still be running,
    /// and a later <see cref="SetPtt"/> is sent before the rest of that write.
    /// </summary>
    bool TryForceFt4DopplerStep(DateTime frequencyAtUtc, TimeSpan timeout);

    /// <summary>
    /// Read the uplink radio’s set RF power in approximate watts when the driver supports it
    /// (e.g. ICOM CI-V 0x14 0x0A mapped via band maximum). Returns false when unsupported,
    /// disconnected, or the read failed.
    /// </summary>
    bool TryGetUplinkRfPowerWatts(out double watts);

    /// <summary>
    /// Set the uplink radio's RF power in watts when the driver can write it
    /// (ICOM CI-V, or Yaesu/Kenwood <c>PC</c>). Returns false when unsupported,
    /// disconnected, or the radio did not accept the command.
    /// </summary>
    bool TrySetUplinkRfPowerWatts(double watts);

    /// <summary>
    /// The next connect must read the radio identity again, even if the port is already open.
    /// Used when leaving standby, before CAT writes resume.
    /// </summary>
    void RequireIdentityRecheck();

    void Disconnect();

    /// <summary>Disconnect and block until the rig worker has torn down drivers and cleared tracking state.</summary>
    void DisconnectAndWait();
}
