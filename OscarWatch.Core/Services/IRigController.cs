using OscarWatch.Core.Cw;
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
    /// Single-VFO radio on a same-band satellite (FT-847): move the dial to the Doppler uplink
    /// before the modem keys, and back to the downlink after <paramref name="hold"/>. A call
    /// during a hold extends it. Returns true once the dial is on the uplink; false when the
    /// radio does not need this or the rig thread did not finish within <paramref name="timeout"/>.
    /// </summary>
    bool TryHoldSameBandTransmit(TimeSpan hold, TimeSpan timeout) => false;

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
    /// Send one piece of CW text that the uplink radio keys itself (Kenwood <c>KY</c>, Icom <c>0x17</c>).
    /// Returns false when the radio cannot, is not connected, or did not accept it within <paramref name="timeout"/>.
    /// </summary>
    bool TrySendCwText(CwMessage piece, TimeSpan timeout) => false;

    /// <summary>Stop CW text the uplink radio is sending.</summary>
    bool TryStopCwText() => false;

    /// <summary>Set the uplink radio's keyer speed for CW text.</summary>
    bool TrySetCwSpeed(int wpm) => false;

    /// <summary>Raise or drop RTS or DTR on the uplink CAT port as a CW key line.</summary>
    bool TrySetCwKeyLine(bool useRts, bool assert) => false;

    /// <summary>
    /// The next connect must read the radio identity again, even if the port is already open.
    /// Used when leaving standby, before CAT writes resume.
    /// </summary>
    void RequireIdentityRecheck();

    void Disconnect();

    /// <summary>Disconnect and block until the rig worker has torn down drivers and cleared tracking state.</summary>
    void DisconnectAndWait();
}
