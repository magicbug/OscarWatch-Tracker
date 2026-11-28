using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using OscarWatch.Core.Sstv;
using OscarWatch.Core.Terminal;
using OscarWatch.Core.Terminal.Modem;
using OscarWatch.Ft4;
using OscarWatch.Hardware;
using OscarWatch.Localization;
using OscarWatch.Rotator;
using OscarWatch.Terminal;
using Serilog;

namespace OscarWatch.ViewModels;

public sealed record TerminalDuplexOption(TerminalDuplexMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record TerminalBitFixOption(AfskBitFixMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record TerminalTestToneOption(AfskTestTone Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record TerminalRateModeOption(BuiltInModemRateMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Built-in soundcard modem section of the Terminal settings dialogue.</summary>
public sealed partial class TerminalBuiltInModemViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TerminalBuiltInModemViewModel>();
    private static readonly TimeSpan TestToneLength = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly ILocalizationService _l;
    private readonly TerminalService _service;
    private readonly IRigController? _rig;
    private readonly Ft4ModemService? _ft4;
    private readonly CwKeyerService? _cw;
    private readonly DispatcherTimer _meterTimer;
    private PacketAudioService? _monitor;
    private CancellationTokenSource? _toneCts;
    private PttTestSession? _pttTest;
    private bool _loadingDevices;
    private string? _meterError;

    /// <summary>
    /// Last non-empty PTT port. The combo's TwoWay binding clears <see cref="SeparatePttPort"/>
    /// when the value is not in <see cref="PttPortOptions"/>, including while that list is rebuilt.
    /// </summary>
    private string _rememberedSeparatePttPort = "";

    public TerminalBuiltInModemViewModel(
        ISettingsService settings,
        ILocalizationService localization,
        TerminalService service,
        IRigController? rig,
        Ft4ModemService? ft4 = null,
        CwKeyerService? cw = null)
    {
        _settings = settings;
        _l = localization;
        _service = service;
        _rig = rig;
        _ft4 = ft4;
        _cw = cw;
        var m = settings.Current.Terminal.BuiltIn;

        PttMethodOptions =
        [
            new PttMethodOption(PttMethod.Vox, _l.Get("Ft4.Ptt.Vox")),
            new PttMethodOption(PttMethod.Cat, _l.Get("Ft4.Ptt.Cat")),
            new PttMethodOption(PttMethod.CatPortHandshake, _l.Get("Ft4.Ptt.CatPortHandshake")),
            new PttMethodOption(PttMethod.SeparateComPort, _l.Get("Ft4.Ptt.SeparateComPort")),
        ];
        PttLineOptions =
        [
            new PttLineOption(PttLine.Rts, _l.Get("Ft4.Ptt.Rts")),
            new PttLineOption(PttLine.Dtr, _l.Get("Ft4.Ptt.Dtr")),
        ];
        DuplexOptions =
        [
            new TerminalDuplexOption(TerminalDuplexMode.Auto, _l.Get("Terminal.Modem.Duplex.Auto")),
            new TerminalDuplexOption(TerminalDuplexMode.Half, _l.Get("Terminal.Modem.Duplex.Half")),
            new TerminalDuplexOption(TerminalDuplexMode.Full, _l.Get("Terminal.Modem.Duplex.Full")),
        ];
        BitFixOptions =
        [
            new TerminalBitFixOption(AfskBitFixMode.Off, _l.Get("Terminal.Modem.BitFix.Off")),
            new TerminalBitFixOption(AfskBitFixMode.SingleBit, _l.Get("Terminal.Modem.BitFix.Single")),
            new TerminalBitFixOption(AfskBitFixMode.SingleAndAdjacentPair, _l.Get("Terminal.Modem.BitFix.Pair")),
        ];
        TestToneOptions =
        [
            new TerminalTestToneOption(AfskTestTone.Alternating, _l.Get("Terminal.Modem.TestTone.Alternating")),
            new TerminalTestToneOption(AfskTestTone.Mark, _l.Get("Terminal.Modem.TestTone.Mark")),
            new TerminalTestToneOption(AfskTestTone.Space, _l.Get("Terminal.Modem.TestTone.Space")),
        ];
        RateModeOptions =
        [
            new TerminalRateModeOption(BuiltInModemRateMode.Auto, _l.Get("Terminal.Modem.RateMode.Auto")),
            new TerminalRateModeOption(BuiltInModemRateMode.Afsk1200, _l.Get("Terminal.Modem.RateMode.Afsk1200")),
            new TerminalRateModeOption(BuiltInModemRateMode.G3ruh9600, _l.Get("Terminal.Modem.RateMode.G3ruh9600")),
        ];

        _txLevelPercent = (int)Math.Round(m.TxLevel * 100);
        _txLevel9600Percent = (int)Math.Round(m.TxLevel9600 * 100);
        _selectedRateMode = RateModeOptions.First(o => o.Value == m.RateMode);
        _selectedPttMethod = PttMethodOptions.FirstOrDefault(o => o.Value == m.PttMethod) ?? PttMethodOptions[0];
        _selectedPttLine = PttLineOptions.FirstOrDefault(o => o.Value == m.PttLine) ?? PttLineOptions[0];
        _pttInvert = m.PttInvert;
        _separatePttPort = (m.SeparatePttPort ?? "").Trim();
        _rememberedSeparatePttPort = _separatePttPort;
        // The combo binds before Refresh() lists ports. An empty list makes the TwoWay
        // selection binding clear the saved port, so keep that port in the list now.
        if (_separatePttPort.Length > 0)
            PttPortOptions.Add(_separatePttPort);
        _pttLeadMs = m.PttLeadMs;
        _pttTailMs = m.PttTailMs;
        _selectedDuplex = DuplexOptions.First(o => o.Value == m.Duplex);
        _waitForClearChannelInFullDuplex = m.WaitForClearChannelInFullDuplex;
        _persist = m.Persist;
        _slotTimeMs = m.SlotTimeMs;
        _maxChannelWaitMs = m.MaxChannelWaitMs;
        _turnaroundGuardMs = m.TurnaroundGuardMs;
        _selectedBitFix = BitFixOptions.First(o => o.Value == m.BitFix);
        _frequencyOffsetDecoders = m.FrequencyOffsetDecoders;
        _watchdogSeconds = m.WatchdogSeconds;
        _blockTxBelowHorizon = m.BlockTxBelowHorizon;
        _selectedTestTone = TestToneOptions[0];

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _meterTimer.Tick += (_, _) => UpdateMeter();
        _meterTimer.Start();
        RefreshG3ruhWarning();
    }

    partial void OnSelectedRateModeChanged(TerminalRateModeOption value) => RefreshG3ruhWarning();

    private void RefreshG3ruhWarning()
    {
        var profileHint = _service.Profile?.ModemHint;
        ShowG3ruhWarning = SelectedRateMode.Value == BuiltInModemRateMode.G3ruh9600
                          || (SelectedRateMode.Value == BuiltInModemRateMode.Auto
                              && TerminalSatelliteProfiles.ModemHintIsG3ruh9600(profileHint));
    }

    public IReadOnlyList<PttMethodOption> PttMethodOptions { get; }
    public IReadOnlyList<PttLineOption> PttLineOptions { get; }
    public IReadOnlyList<TerminalDuplexOption> DuplexOptions { get; }
    public IReadOnlyList<TerminalBitFixOption> BitFixOptions { get; }
    public IReadOnlyList<TerminalTestToneOption> TestToneOptions { get; }
    public IReadOnlyList<TerminalRateModeOption> RateModeOptions { get; }
    public ObservableCollection<Ft4AudioDeviceOption> InputDeviceOptions { get; } = [];
    public ObservableCollection<Ft4AudioDeviceOption> OutputDeviceOptions { get; } = [];
    public ObservableCollection<string> PttPortOptions { get; } = [];
    public ObservableCollection<string> DecodedFrames { get; } = [];

    [ObservableProperty] private Ft4AudioDeviceOption? _selectedInputDevice;
    [ObservableProperty] private Ft4AudioDeviceOption? _selectedOutputDevice;
    [ObservableProperty] private int _txLevelPercent;
    [ObservableProperty] private int _txLevel9600Percent;
    [ObservableProperty] private TerminalRateModeOption _selectedRateMode;
    [ObservableProperty] private bool _showG3ruhWarning;
    [ObservableProperty] private PttMethodOption _selectedPttMethod;
    [ObservableProperty] private PttLineOption _selectedPttLine;
    [ObservableProperty] private bool _pttInvert;
    [ObservableProperty] private string _separatePttPort;
    [ObservableProperty] private int _pttLeadMs;
    [ObservableProperty] private int _pttTailMs;
    [ObservableProperty] private string _separatePttConflictText = "";
    [ObservableProperty] private TerminalDuplexOption _selectedDuplex;
    [ObservableProperty] private bool _waitForClearChannelInFullDuplex;
    [ObservableProperty] private int _persist;
    [ObservableProperty] private int _slotTimeMs;
    [ObservableProperty] private int _maxChannelWaitMs;
    [ObservableProperty] private int _turnaroundGuardMs;
    [ObservableProperty] private TerminalBitFixOption _selectedBitFix;
    [ObservableProperty] private bool _frequencyOffsetDecoders;
    [ObservableProperty] private int _watchdogSeconds;
    [ObservableProperty] private bool _blockTxBelowHorizon;
    [ObservableProperty] private TerminalTestToneOption _selectedTestTone;
    [ObservableProperty] private bool _isSendingTestTone;
    [ObservableProperty] private string _testToneText = "";
    [ObservableProperty] private bool _isTestingPtt;
    [ObservableProperty] private string _pttTestText = "";
    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private double _inputLevelPercent;
    [ObservableProperty] private string _inputLevelText = "";
    [ObservableProperty] private bool _isDecodingWav;
    [ObservableProperty] private string _decodeSummary = "";

    /// <summary>Set by the window: shows a file picker and returns the chosen WAV path.</summary>
    public Func<Task<string?>>? PickWavFile { get; set; }

    public bool ShowPttLine => SelectedPttMethod.Value is PttMethod.CatPortHandshake or PttMethod.SeparateComPort;
    public bool ShowSeparatePttPort => SelectedPttMethod.Value == PttMethod.SeparateComPort;
    public bool ShowPttTiming => SelectedPttMethod.Value != PttMethod.Vox;
    public bool ShowSeparatePttConflict => ShowSeparatePttPort && SeparatePttConflictText.Length > 0;
    public bool ShowHalfDuplexOptions => SelectedDuplex.Value != TerminalDuplexMode.Full || WaitForClearChannelInFullDuplex;
    public bool HasDecodedFrames => DecodedFrames.Count > 0;

    /// <summary>Label for the Test PTT button: Stop while keyed, otherwise Test PTT.</summary>
    public string PttTestButtonText =>
        IsTestingPtt ? _l.Get("Terminal.Modem.TestTone.Stop") : _l.Get("Terminal.Modem.Ptt.Test");

    /// <summary>True when the operator can press Test PTT (or Stop while a test is running).</summary>
    public bool CanTestPtt => IsTestingPtt || CurrentPttTestBlockReason() == PttTestBlockReason.None;

    partial void OnSelectedPttMethodChanged(PttMethodOption value)
    {
        OnPropertyChanged(nameof(ShowPttLine));
        OnPropertyChanged(nameof(ShowSeparatePttPort));
        OnPropertyChanged(nameof(ShowPttTiming));
        RefreshSeparatePttConflict();
        RefreshPttTestEnablement();
    }

    partial void OnIsTestingPttChanged(bool value)
    {
        OnPropertyChanged(nameof(PttTestButtonText));
        OnPropertyChanged(nameof(CanTestPtt));
    }

    partial void OnIsSendingTestToneChanged(bool value) => RefreshPttTestEnablement();

    partial void OnSeparatePttPortChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _rememberedSeparatePttPort = value.Trim();
        RefreshSeparatePttConflict();
    }

    partial void OnSeparatePttConflictTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSeparatePttConflict));
        RefreshPttTestEnablement();
    }

    partial void OnSelectedDuplexChanged(TerminalDuplexOption value) => OnPropertyChanged(nameof(ShowHalfDuplexOptions));

    partial void OnWaitForClearChannelInFullDuplexChanged(bool value) => OnPropertyChanged(nameof(ShowHalfDuplexOptions));

    partial void OnSelectedInputDeviceChanged(Ft4AudioDeviceOption? value)
    {
        if (!_loadingDevices && IsMonitoring)
            StartMonitor();
    }

    partial void OnIsMonitoringChanged(bool value)
    {
        if (value)
            StartMonitor();
        else
            StopMonitor();
    }

    private void RefreshSeparatePttConflict()
    {
        SeparatePttConflictText = ShowSeparatePttPort
            && SerialPortConflictHelper.TryDescribeTerminalSeparatePttConflict(_settings.Current, SeparatePttPort, out var message)
                ? ComPortConflictLocalizer.Localize(message, _l)
                : "";
    }

    /// <summary>Lists soundcards and COM ports. Opening PortAudio can take a moment, so the window calls this once shown.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var m = _settings.Current.Terminal.BuiltIn;
        _loadingDevices = true;
        try
        {
            using var probe = new PacketAudioService();
            var defaultLabel = _l.Get("Ft4.Audio.SystemDefault");
            InputDeviceOptions.Clear();
            InputDeviceOptions.Add(new Ft4AudioDeviceOption("", defaultLabel));
            foreach (var d in probe.GetInputDevices())
                InputDeviceOptions.Add(new Ft4AudioDeviceOption(d.Id, d.DisplayName));
            OutputDeviceOptions.Clear();
            OutputDeviceOptions.Add(new Ft4AudioDeviceOption("", defaultLabel));
            foreach (var d in probe.GetOutputDevices())
                OutputDeviceOptions.Add(new Ft4AudioDeviceOption(d.Id, d.DisplayName));

            SelectedInputDevice = Find(InputDeviceOptions, SelectedInputDevice?.Id ?? m.InputDeviceId, m.InputDeviceDisplayName);
            SelectedOutputDevice = Find(OutputDeviceOptions, SelectedOutputDevice?.Id ?? m.OutputDeviceId, m.OutputDeviceDisplayName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal modem device list failed");
        }
        finally
        {
            _loadingDevices = false;
        }

        var savedPort = SeparatePttPort?.Trim() ?? "";
        if (savedPort.Length == 0)
            savedPort = _rememberedSeparatePttPort;
        PttPortOptions.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePorts(forceRefresh: true))
            PttPortOptions.Add(port);
        var listed = PttPortOptions.FirstOrDefault(port =>
            string.Equals(port, savedPort, StringComparison.OrdinalIgnoreCase));
        if (listed is null && savedPort.Length > 0)
        {
            PttPortOptions.Insert(0, savedPort);
            listed = savedPort;
        }

        // Clear() above drops the combo selection. Assign the string that is actually in the list.
        SeparatePttPort = listed ?? "";
        RefreshSeparatePttConflict();
    }

    private static Ft4AudioDeviceOption Find(IList<Ft4AudioDeviceOption> options, string? id, string? name) =>
        options.FirstOrDefault(o => id is { Length: > 0 } && string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? options.FirstOrDefault(o => name is { Length: > 0 } && string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase))
        ?? options[0];

    private void StartMonitor()
    {
        StopMonitor();
        // A second capture on a device the modem already has open can read silence.
        if (ModemConnected)
            return;
        try
        {
            _monitor = new PacketAudioService();
            _monitor.StartCapture(SelectedInputDevice?.Id, SelectedInputDevice?.DisplayName);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Terminal modem level meter could not open the input");
            StopMonitor();
            _meterError = _l.Get("Terminal.Modem.Level.Failed", ex.Message);
        }
    }

    private bool ModemConnected => _service.BuiltInModem is { State: TerminalTransportState.Connected };

    private string OutputDeviceLabel()
    {
        var name = SelectedOutputDevice?.DisplayName?.Trim();
        return string.IsNullOrWhiteSpace(name) ? _l.Get("Terminal.Modem.Soundcard") : name;
    }

    private void StopMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        _meterError = null;
    }

    private void UpdateMeter()
    {
        double? peak = null;
        if (ModemConnected)
        {
            if (_monitor is not null)
                StopMonitor();
        }
        else if (IsMonitoring && _monitor is null && _meterError is null)
        {
            StartMonitor();
        }

        if (_service.BuiltInModem is { State: TerminalTransportState.Connected } modem)
        {
            peak = modem.InputLevel;
        }
        else if (_monitor is { } monitor)
        {
            var scratch = new float[4096];
            while (monitor.ReadCapture(scratch) > 0)
            {
            }

            peak = monitor.InputPeak;
        }

        if (peak is not { } p)
        {
            InputLevelPercent = 0;
            InputLevelText = _meterError ?? _l.Get("Terminal.Modem.Level.Idle");
        }
        else
        {
            InputLevelPercent = Math.Round(p * 100);
            InputLevelText = p switch
            {
                >= 0.98 => _l.Get("Terminal.Modem.Level.Clipping", InputLevelPercent),
                < 0.05 => _l.Get("Terminal.Modem.Level.Low", InputLevelPercent),
                _ => _l.Get("Terminal.Modem.Level.Ok", InputLevelPercent)
            };
        }

        RefreshPttTestEnablement();
    }

    private PttOptions SnapshotPttOptions() =>
        new(SelectedPttMethod.Value, SelectedPttLine.Value, PttInvert, SeparatePttPort ?? "", PttLeadMs, PttTailMs);

    private PttTestBlockReason CurrentPttTestBlockReason() =>
        PttTestSession.GetBlockReason(
            SelectedPttMethod.Value,
            hasRig: _rig is not null,
            rigConnected: _rig?.GetStatus().IsConnected == true,
            terminalTransmitting: _service.BuiltInModem?.IsTransmitting == true,
            ft4Active: _ft4?.IsTransmissionActive == true,
            cwSending: _cw?.IsSending == true,
            testToneBusy: IsSendingTestTone,
            separatePortConflict: ShowSeparatePttConflict,
            sessionActive: false);

    private void RefreshPttTestEnablement()
    {
        OnPropertyChanged(nameof(CanTestPtt));
        if (IsTestingPtt)
            return;

        var block = CurrentPttTestBlockReason();
        if (block is PttTestBlockReason.NoRig or PttTestBlockReason.RigDisconnected)
            PttTestText = _l.Get("Terminal.Modem.Ptt.Test.NeedRig");
        else if (block is PttTestBlockReason.TerminalBusy or PttTestBlockReason.Ft4Busy or PttTestBlockReason.CwBusy)
            PttTestText = _l.Get("Terminal.Modem.Ptt.Test.Busy");
        else if (block == PttTestBlockReason.TestToneBusy)
            PttTestText = _l.Get("Terminal.Modem.Ptt.Test.Busy");
        else if (block == PttTestBlockReason.SeparatePortConflict && SeparatePttConflictText.Length > 0)
            PttTestText = SeparatePttConflictText;
        else if (PttTestText == _l.Get("Terminal.Modem.Ptt.Test.NeedRig")
                 || PttTestText == _l.Get("Terminal.Modem.Ptt.Test.Busy")
                 || (SeparatePttConflictText.Length > 0 && PttTestText == SeparatePttConflictText))
            PttTestText = "";
    }

    /// <summary>Keys the radio with the dialogue's PTT settings for two seconds, without audio.</summary>
    [RelayCommand]
    private async Task TestPttAsync()
    {
        if (IsTestingPtt)
        {
            _pttTest?.Cancel();
            return;
        }

        if (SelectedPttMethod.Value == PttMethod.Vox)
        {
            PttTestText = _l.Get("Terminal.Modem.Ptt.Test.Vox");
            return;
        }

        var block = CurrentPttTestBlockReason();
        if (block != PttTestBlockReason.None)
        {
            RefreshPttTestEnablement();
            return;
        }

        if (_rig is null)
            return;

        var options = SnapshotPttOptions();
        var session = new PttTestSession(
            _rig,
            options,
            port => SerialPortConflictHelper.TryDescribeTerminalSeparatePttConflict(_settings.Current, port, out var m) ? m : null,
            PttSessionMarker.ForTerminal(_settings.SettingsPath));
        _pttTest = session;
        IsTestingPtt = true;
        PttTestText = _l.Get("Terminal.Modem.Ptt.Test.Keyed");
        try
        {
            var outcome = await session.RunAsync().ConfigureAwait(true);
            PttTestText = outcome switch
            {
                PttTestOutcome.RigDisconnected => _l.Get("Terminal.Modem.Ptt.Test.NeedRig"),
                PttTestOutcome.Cancelled => _l.Get("Terminal.Modem.Ptt.Test.Unkeyed"),
                _ => _l.Get("Terminal.Modem.Ptt.Test.Unkeyed"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Log.Information(ex, "Terminal PTT test failed");
            PttTestText = _l.Get("Terminal.Modem.Ptt.Test.Failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_pttTest, session))
                _pttTest = null;
            session.Dispose();
            IsTestingPtt = false;
            RefreshPttTestEnablement();
        }
    }

    /// <summary>Keys the radio with the dialogue's PTT settings and plays a tone for setting the deviation.</summary>
    [RelayCommand]
    private async Task SendTestToneAsync()
    {
        if (IsSendingTestTone)
            return;
        if (IsTestingPtt)
            return;
        if (_rig is null)
            return;

        IsSendingTestTone = true;
        TestToneText = _l.Get("Terminal.Modem.TestTone.Sending");
        var cts = new CancellationTokenSource();
        _toneCts = cts;
        var options = SnapshotPttOptions();
        using var audio = new PacketAudioService();
        using var ptt = new PttKeyer(_rig, () => options, "Terminal test tone",
            port => SerialPortConflictHelper.TryDescribeTerminalSeparatePttConflict(_settings.Current, port, out var m) ? m : null);
        try
        {
            audio.StartPlayback(SelectedOutputDevice?.Id, SelectedOutputDevice?.DisplayName);
            var useG3 = SelectedRateMode.Value switch
            {
                BuiltInModemRateMode.G3ruh9600 => true,
                BuiltInModemRateMode.Afsk1200 => false,
                _ => TerminalSatelliteProfiles.ModemHintIsG3ruh9600(_service.Profile?.ModemHint),
            };
            var tone = useG3
                ? G3ruhModulator.TestPattern(TestToneLength.TotalSeconds, audio.PlaybackSampleRate,
                    Math.Clamp(TxLevel9600Percent / 100.0, 0.01, 1))
                : AfskModulator.TestTone(SelectedTestTone.Value, TestToneLength.TotalSeconds, audio.PlaybackSampleRate,
                    Math.Clamp(TxLevelPercent / 100.0, 0.01, 1));
            await ptt.KeyAsync(cts.Token).ConfigureAwait(true);
            audio.Enqueue(tone);
            await audio.WaitForDrainAsync(cts.Token).WaitAsync(TestToneLength + TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(audio.OutputLatencySeconds), cts.Token).ConfigureAwait(true);
            TestToneText = _l.Get("Terminal.Modem.TestTone.Done");
        }
        catch (OperationCanceledException)
        {
            TestToneText = _l.Get("Terminal.Modem.TestTone.Stopped");
        }
        catch (Exception ex)
        {
            if (PacketAudioOpenFailure.IsStreamOpenFailure(ex))
            {
                var device = OutputDeviceLabel();
                Log.Warning(
                    "Terminal test tone could not open '{Device}' (modem connected={Connected})",
                    device,
                    ModemConnected);
                TestToneText = _l.Get(
                    ModemConnected
                        ? "Terminal.Modem.TestTone.SoundcardBusy"
                        : "Terminal.Modem.TestTone.SoundcardBusyOther",
                    device);
            }
            else
            {
                Log.Information(ex, "Terminal test tone failed");
                TestToneText = _l.Get("Terminal.Modem.TestTone.Failed", ex.Message);
            }
        }
        finally
        {
            audio.ClearPlayback();
            ptt.UnkeyNow();
            _toneCts = null;
            cts.Dispose();
            IsSendingTestTone = false;
        }
    }

    [RelayCommand]
    private void StopTestTone() => _toneCts?.Cancel();

    /// <summary>Decodes a recording offline. Nothing goes to the log, APRS-IS, UHM or the logbook.</summary>
    [RelayCommand]
    private async Task DecodeWavAsync()
    {
        if (IsDecodingWav || PickWavFile is null)
            return;
        var path = await PickWavFile().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsDecodingWav = true;
        DecodedFrames.Clear();
        OnPropertyChanged(nameof(HasDecodedFrames));
        DecodeSummary = _l.Get("Terminal.Modem.Decode.Running", Path.GetFileName(path));
        var bitFix = SelectedBitFix.Value;
        try
        {
            var offsetDecoders = FrequencyOffsetDecoders;
            var result = await Task.Run(() => DecodeFile(path, bitFix, offsetDecoders)).ConfigureAwait(true);
            foreach (var line in result.Lines)
                DecodedFrames.Add(line);
            OnPropertyChanged(nameof(HasDecodedFrames));
            DecodeSummary = _l.Get("Terminal.Modem.Decode.Summary", result.Frames, result.Fixed, result.Seconds.ToString("0"), result.Paths);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                                       or UnauthorizedAccessException or EndOfStreamException)
        {
            Log.Information(ex, "Terminal WAV decode failed");
            DecodeSummary = _l.Get("Terminal.Modem.Decode.Failed", ex.Message);
        }
        finally
        {
            IsDecodingWav = false;
        }
    }

    private sealed record DecodeResult(List<string> Lines, int Frames, int Fixed, double Seconds, string Paths);

    private static DecodeResult DecodeFile(string path, AfskBitFixMode bitFix, bool frequencyOffsetDecoders)
    {
        var (samples, rate) = SstvWavReader.Read(path);
        var receiver = new AfskReceiver(rate, bitFix, frequencyOffsetDecoders);
        var frames = new List<AfskDecodedFrame>();
        const int block = 8192;
        for (var i = 0; i < samples.Length; i += block)
            receiver.Process(samples.AsSpan(i, Math.Min(block, samples.Length - i)), frames);
        receiver.Process(new float[rate / 10], frames);
        receiver.Flush(frames);

        var lines = new List<string>(frames.Count);
        foreach (var f in frames)
        {
            var text = Ax25Frame.TryParse(f.Ax25, out var frame) && frame is not null
                ? frame.ToMonitorString()
                : Convert.ToHexString(f.Ax25);
            lines.Add($"{TimeSpan.FromSeconds(f.EndSeconds):hh\\:mm\\:ss}  {f.Quality,3}%  {text}");
        }

        var paths = string.Join(", ", Enumerable.Range(0, receiver.PathCount)
            .Where(p => receiver.PathDecodeCount(p) > 0)
            .Select(p => $"{receiver.PathName(p)} {receiver.PathDecodeCount(p)}"));
        return new DecodeResult(lines, receiver.FrameCount, receiver.FixedCount, samples.Length / (double)rate, paths);
    }

    public void ApplyTo(TerminalBuiltInModemSettings m)
    {
        if (SelectedInputDevice is { } input)
        {
            m.InputDeviceId = input.Id;
            m.InputDeviceDisplayName = input.Id.Length > 0 ? input.DisplayName : "";
        }

        if (SelectedOutputDevice is { } output)
        {
            m.OutputDeviceId = output.Id;
            m.OutputDeviceDisplayName = output.Id.Length > 0 ? output.DisplayName : "";
        }

        m.TxLevel = TxLevelPercent / 100.0;
        m.TxLevel9600 = TxLevel9600Percent / 100.0;
        m.RateMode = SelectedRateMode.Value;
        m.PttMethod = SelectedPttMethod.Value;
        m.PttLine = SelectedPttLine.Value;
        m.PttInvert = PttInvert;
        m.SeparatePttPort = SeparatePttPort ?? "";
        m.PttLeadMs = PttLeadMs;
        m.PttTailMs = PttTailMs;
        m.Duplex = SelectedDuplex.Value;
        m.WaitForClearChannelInFullDuplex = WaitForClearChannelInFullDuplex;
        m.Persist = Persist;
        m.SlotTimeMs = SlotTimeMs;
        m.MaxChannelWaitMs = MaxChannelWaitMs;
        m.TurnaroundGuardMs = TurnaroundGuardMs;
        m.BitFix = SelectedBitFix.Value;
        m.FrequencyOffsetDecoders = FrequencyOffsetDecoders;
        m.WatchdogSeconds = WatchdogSeconds;
        m.BlockTxBelowHorizon = BlockTxBelowHorizon;
        m.Normalise();
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _toneCts?.Cancel();
        _pttTest?.Cancel();
        _pttTest?.Dispose();
        _pttTest = null;
        StopMonitor();
    }
}
