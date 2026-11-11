using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests.Hardware;

/// <summary>Fake rig that records PTT and same-band hold calls.</summary>
internal sealed class RecordingRigController : IRigController
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public bool Connected { get; set; } = true;

    /// <summary>Result of <see cref="TryHoldSameBandTransmit"/>.</summary>
    public bool SameBandHoldResult { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToArray();
        }
    }

    private void Record(string call)
    {
        lock (_gate)
            _calls.Add(call);
    }

    public RigConnectionStatus GetStatus() =>
        new(Connected, false, RigStatusKind.Disconnected, null, null, null, null);

    public void SetPtt(bool transmit) => Record(transmit ? "ptt:on" : "ptt:off");
    public void SetHandshakePtt(bool useRts, bool assert) => Record($"{(useRts ? "rts" : "dtr")}:{(assert ? "on" : "off")}");

    public bool TryHoldSameBandTransmit(TimeSpan hold, TimeSpan timeout)
    {
        Record("hold");
        return SameBandHoldResult;
    }

    public void PublishContext(RigSettings settings, RigTrackingContext? context, bool reinitializePass = false, bool? catPausedOverride = null) { }
    public void Update(RigSettings settings, RigTrackingContext? context) { }
    public void ApplySelectedCtcss(RigSettings settings, RigTrackingContext? context) { }
    public void SetFt4SlotGatedDoppler(bool hold) { }
    public void ForceFt4DopplerStep() { }
    public bool TryForceFt4DopplerStep(DateTime frequencyAtUtc, TimeSpan timeout) => true;
    public bool TryGetUplinkRfPowerWatts(out double watts)
    {
        watts = 0;
        return false;
    }
    public bool TrySetUplinkRfPowerWatts(double watts) => false;

    /// <summary>Result of the CW text and key line calls.</summary>
    public bool CwResult { get; set; } = true;

    public bool TrySendCwText(CwMessage piece, TimeSpan timeout)
    {
        Record("cwtext:" + piece.DisplayText);
        return CwResult;
    }

    public bool TryStopCwText()
    {
        Record("cwstop");
        return CwResult;
    }

    public bool TrySetCwSpeed(int wpm)
    {
        Record($"cwspeed:{wpm}");
        return CwResult;
    }

    public bool TrySetCwKeyLine(bool useRts, bool assert)
    {
        Record($"cwkey:{(useRts ? "rts" : "dtr")}:{(assert ? "on" : "off")}");
        return CwResult;
    }
    public void RequireIdentityRecheck() { }
    public void Disconnect() { }
    public void DisconnectAndWait() { }
}
