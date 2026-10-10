using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

internal sealed class RecordingRigDriver : IRigDriver
{
    public long MainHz { get; set; }
    public long SubHz { get; set; }

    /// <summary>When set, the next Main/VfoA read returns this value once (simulates CAT display lag).</summary>
    public long? NextStaleMainReadHz { get; set; }

    /// <summary>
    /// When set, every Main/VfoA read returns this value (simulates lagging Flex/SmartSDR status
    /// that keeps reporting the pre-write frequency after SetFrequencyHz succeeds).
    /// </summary>
    public long? ForcedMainReadHz { get; set; }
    public int SetFrequencyCallCount { get; private set; }
    public Queue<bool> SetFrequencyResults { get; } = new();
    public double? LastToneHz { get; private set; }
    public bool? LastToneSquelch { get; private set; }
    public bool ToneSquelchOn { get; private set; }
    public bool ToneOn { get; private set; }

    public RigType RigType => RigType.Dummy;
    public bool IsConnected => true;
    public bool SupportsTracking => true;
    public bool ConfirmIdentity { get; set; } = true;
    public int OpenCallCount { get; private set; }
    public bool TryConfirmIdentity() => ConfirmIdentity;
    public bool IsSatelliteModeActive { get; set; } = true;

    public void Open() => OpenCallCount++;

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        if (vfo is RigVfo.VfoA or RigVfo.Main)
        {
            if (ForcedMainReadHz is long forced)
                return forced;

            if (NextStaleMainReadHz is long stale)
            {
                NextStaleMainReadHz = null;
                return stale;
            }

            return MainHz;
        }

        return SubHz;
    }

    public bool SetFrequencyHz(long hz)
    {
        SetFrequencyCallCount++;
        var success = SetFrequencyResults.Count > 0 ? SetFrequencyResults.Dequeue() : true;
        if (!success)
            return false;

        if (_currentVfo is RigVfo.VfoA or RigVfo.Main)
            MainHz = hz;
        else
            SubHz = hz;
        return true;
    }

    private RigVfo _currentVfo = RigVfo.Main;

    public RigVfo CurrentVfo => _currentVfo;
    public int SelectVfoCallCount { get; private set; }
    public bool LastSelectVfoForce { get; private set; }

    public RigVfo? LastToneVfo { get; private set; }

    public void SelectVfo(RigVfo vfo, bool force = false)
    {
        SelectVfoCallCount++;
        LastSelectVfoForce = force;
        _currentVfo = vfo;
    }

    public RigVfo? LastModeVfo { get; private set; }
    public int ModeSetCount { get; private set; }

    public void SetMode(string mode)
    {
        ModeSetCount++;
        LastModeVfo = _currentVfo;
    }
    public int SetSplitOnCallCount { get; private set; }
    public bool? LastSplitOn { get; private set; }

    public void SetSplitOn(bool on)
    {
        SetSplitOnCallCount++;
        LastSplitOn = on;
    }
    public int SetSatelliteModeCallCount { get; private set; }
    public bool? LastSatelliteModeOn { get; private set; }

    public void SetSatelliteMode(bool on)
    {
        SetSatelliteModeCallCount++;
        LastSatelliteModeOn = on;
    }
    public int ExchangeVfoCallCount { get; private set; }

    public void ExchangeVfos()
    {
        ExchangeVfoCallCount++;
        (MainHz, SubHz) = (SubHz, MainHz);
    }
    public RigVfo? LastToneOffVfo { get; private set; }

    private readonly List<RigVfo> _toneClearedVfos = [];

    public IReadOnlyList<RigVfo> ToneClearedVfos => _toneClearedVfos;

    public void SetToneOn(bool on)
    {
        if (!on)
        {
            LastToneOffVfo = _currentVfo;
            RecordToneClearedVfo(_currentVfo);
        }

        ToneOn = on;
    }

    public void SetToneSquelchOn(bool on)
    {
        if (!on)
        {
            LastToneOffVfo = _currentVfo;
            RecordToneClearedVfo(_currentVfo);
        }

        ToneSquelchOn = on;
    }

    private void RecordToneClearedVfo(RigVfo vfo)
    {
        if (!_toneClearedVfos.Contains(vfo))
            _toneClearedVfos.Add(vfo);
    }

    public void SetToneHz(double hz, bool squelchTone)
    {
        LastToneHz = hz;
        LastToneSquelch = squelchTone;
        LastToneVfo = _currentVfo;
    }
    public void Dispose() { }
}
