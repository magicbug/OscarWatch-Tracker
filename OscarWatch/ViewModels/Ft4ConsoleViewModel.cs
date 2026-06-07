using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Services;
using OscarWatch.Ft4.Core;
using OscarWatch.Ft4.Core.Coding;
using OscarWatch.Ft4.Core.Models;
using OscarWatch.Ft4.Core.Services;

namespace OscarWatch.ViewModels;

public partial class Ft4ConsoleViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IFt4AudioService _audio;
    private readonly Ft4Engine _engine;
    private readonly DispatcherTimer _uiTimer;
    private Ft4WaterfallTarget? _waterfall;
    private Action<byte[]>? _spectrumHandler;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _txEnabled;

    [ObservableProperty]
    private bool _txOdd = true;

    public bool TxEven
    {
        get => !TxOdd;
        set => TxOdd = !value;
    }

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private int _rxFrequencyHz = 1500;

    [ObservableProperty]
    private int _txFrequencyHz = 1500;

    [ObservableProperty]
    private double _slotProgress;

    [ObservableProperty]
    private double _txWindowProgress;

    [ObservableProperty]
    private bool _isOddSlot = true;

    [ObservableProperty]
    private string _statusText = "FT4 console idle";

    [ObservableProperty]
    private string _currentTxMessage = "";

    [ObservableProperty]
    private string _callsign = "NOCALL";

    [ObservableProperty]
    private string _gridSquare = "FN03";

    [ObservableProperty]
    private string _selectedInputDeviceId = "";

    [ObservableProperty]
    private string _selectedOutputDeviceId = "";

    [ObservableProperty]
    private float _txGain = 1.0f;

    public ObservableCollection<Ft4MessageListItem> Messages { get; } = [];
    public ObservableCollection<Ft4AudioDevice> InputDevices { get; } = [];
    public ObservableCollection<Ft4AudioDevice> OutputDevices { get; } = [];

    public bool IsNativeCodec => _engine.IsNativeCoder;
    public bool CanMacroDe => _engine.Sequencer.IsMessageAvailable(Ft4MessageType.De);
    public bool CanMacroDb => _engine.Sequencer.IsMessageAvailable(Ft4MessageType.dB);
    public bool CanMacroRDb => _engine.Sequencer.IsMessageAvailable(Ft4MessageType.R_dB);
    public bool CanMacroRr73 => _engine.Sequencer.IsMessageAvailable(Ft4MessageType.RR73);
    public bool CanMacro73 => _engine.Sequencer.IsMessageAvailable(Ft4MessageType._73);

    public Ft4ConsoleViewModel(ISettingsService settings, IFt4AudioService audio, IFt4Coder coder)
    {
        _settings = settings;
        _audio = audio;
        var ft4 = settings.Current.Ft4;
        var station = settings.Current.GroundStation;
        _engine = new Ft4Engine(
            coder,
            audio,
            string.IsNullOrWhiteSpace(ft4.Callsign) ? "NOCALL" : ft4.Callsign,
            string.IsNullOrWhiteSpace(ft4.GridSquare) ? station.GridSquare : ft4.GridSquare);

        _engine.MessageDecoded += OnMessageDecoded;
        _engine.StatusChanged += text => Dispatcher.UIThread.Post(() => StatusText = text);
        _engine.StateChanged += () => Dispatcher.UIThread.Post(RefreshState);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateSlotClock();
    }

    public void AttachWaterfall(Ft4WaterfallTarget waterfall) => _waterfall = waterfall;

    public void Initialize()
    {
        RefreshDeviceLists();
        var ft4 = _settings.Current.Ft4;
        var station = _settings.Current.GroundStation;
        Callsign = string.IsNullOrWhiteSpace(ft4.Callsign) ? "NOCALL" : ft4.Callsign;
        GridSquare = string.IsNullOrWhiteSpace(ft4.GridSquare) ? station.GridSquare : ft4.GridSquare;
        RxFrequencyHz = ft4.RxAudioFrequencyHz;
        TxFrequencyHz = ft4.TxAudioFrequencyHz;
        TxOdd = ft4.TxOdd;
        TxGain = ft4.TxGain;
        _engine.Sequencer.MyCall = Callsign;
        _engine.Sequencer.MySquare = GridSquare;
        SelectedInputDeviceId = ft4.InputDeviceId;
        SelectedOutputDeviceId = ft4.OutputDeviceId;
        _engine.RxAudioFrequencyHz = RxFrequencyHz;
        _engine.TxAudioFrequencyHz = TxFrequencyHz;
        _engine.TxOdd = TxOdd;
        _engine.TxGain = TxGain;
        CurrentTxMessage = _engine.Sequencer.Message ?? $"CQ {_engine.Sequencer.MyCall} {_engine.Sequencer.MySquare}";
    }

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (IsRunning)
        {
            _uiTimer.Stop();
            _engine.HaltTx();
            TxEnabled = false;
            if (_engine.Spectrum is not null && _spectrumHandler is not null)
                _engine.Spectrum.SpectrumRowReady -= _spectrumHandler;
            await _engine.StopAsync();
            IsRunning = false;
            StatusText = "FT4 stopped";
            return;
        }

        if (!_audio.IsAvailable)
        {
            StatusText = _audio.UnavailableReason ?? "Audio unavailable";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedInputDeviceId) || string.IsNullOrWhiteSpace(SelectedOutputDeviceId))
        {
            StatusText = "Select input and output audio devices";
            return;
        }

        PersistSettings();
        var settings = BuildSettings();
        try
        {
            await _engine.StartAsync(settings);
            if (_engine.Spectrum is not null)
            {
                _spectrumHandler = row => _waterfall?.PushRow(row);
                _engine.Spectrum.SpectrumRowReady += _spectrumHandler;
            }

            IsRunning = true;
            _uiTimer.Start();
            StatusText = "FT4 running";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void ToggleTx()
    {
        if (!IsRunning)
            return;

        TxEnabled = !TxEnabled;
        if (TxEnabled)
        {
            _engine.ApplySequencerMessage();
            _engine.StartSending();
        }
        else
        {
            _engine.HaltTx();
        }

        RefreshState();
    }

    [RelayCommand]
    private void HaltTx()
    {
        TxEnabled = false;
        _engine.HaltTx();
        RefreshState();
    }

    [RelayCommand]
    private void Tune()
    {
        if (!IsRunning)
            return;

        TxEnabled = false;
        _engine.HaltTx();
        _engine.StartTuning();
        RefreshState();
    }

    [RelayCommand]
    private void MacroCq() => ForceMacro(Ft4MessageType.CQ);

    [RelayCommand]
    private void MacroDe() => ForceMacro(Ft4MessageType.De);

    [RelayCommand]
    private void MacroDb() => ForceMacro(Ft4MessageType.dB);

    [RelayCommand]
    private void MacroRDb() => ForceMacro(Ft4MessageType.R_dB);

    [RelayCommand]
    private void MacroRr73() => ForceMacro(Ft4MessageType.RR73);

    [RelayCommand]
    private void Macro73() => ForceMacro(Ft4MessageType._73);

    [RelayCommand]
    private void ReplyToMessage(Ft4MessageListItem? item)
    {
        if (item?.Line is null || item.Line.IsTransmit)
            return;

        if (_engine.Sequencer.ProcessMessage(item.Line, forceReply: true))
        {
            CurrentTxMessage = _engine.Sequencer.Message ?? CurrentTxMessage;
            if (TxEnabled)
                _engine.ApplySequencerMessage();
            NotifyMacroAvailability();
        }
    }

    partial void OnRxFrequencyHzChanged(int value)
    {
        _engine.RxAudioFrequencyHz = value;
        PersistSettings();
    }

    partial void OnTxFrequencyHzChanged(int value)
    {
        _engine.TxAudioFrequencyHz = value;
        PersistSettings();
    }

    partial void OnTxOddChanged(bool value)
    {
        _engine.TxOdd = value;
        OnPropertyChanged(nameof(TxEven));
        PersistSettings();
    }

    partial void OnTxGainChanged(float value)
    {
        _engine.TxGain = value;
        PersistSettings();
    }

    partial void OnSelectedInputDeviceIdChanged(string value) => PersistSettings();
    partial void OnSelectedOutputDeviceIdChanged(string value) => PersistSettings();

    partial void OnCallsignChanged(string value)
    {
        _engine.Sequencer.MyCall = value;
        CurrentTxMessage = _engine.Sequencer.Message ?? CurrentTxMessage;
        PersistSettings();
        NotifyMacroAvailability();
    }

    partial void OnGridSquareChanged(string value)
    {
        _engine.Sequencer.MySquare = value;
        CurrentTxMessage = _engine.Sequencer.Message ?? CurrentTxMessage;
        PersistSettings();
        NotifyMacroAvailability();
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _engine.HaltTx();
        if (IsRunning)
            _ = _engine.StopAsync();
        _engine.Dispose();
    }

    private void ForceMacro(Ft4MessageType type)
    {
        if (!_engine.Sequencer.ForceMessage(type))
            return;

        CurrentTxMessage = _engine.Sequencer.Message ?? CurrentTxMessage;
        if (TxEnabled)
            _engine.ApplySequencerMessage();
        NotifyMacroAvailability();
    }

    private void OnMessageDecoded(Ft4DecodeLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Message) && string.IsNullOrWhiteSpace(line.RawText))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Messages.Insert(0, Ft4MessageListItem.FromLine(line));
            while (Messages.Count > 200)
                Messages.RemoveAt(Messages.Count - 1);
        });
    }

    private void UpdateSlotClock()
    {
        var slot = _engine.GetCurrentSlot();
        SlotProgress = slot.SlotProgress;
        TxWindowProgress = slot.TxWindowProgress;
        IsOddSlot = slot.IsOdd;
        IsTransmitting = _engine.IsTransmitting;
    }

    private void RefreshState()
    {
        IsTransmitting = _engine.IsTransmitting;
        TxEnabled = _engine.SenderMode == Ft4SenderMode.Sending;
        NotifyMacroAvailability();
    }

    private void NotifyMacroAvailability()
    {
        OnPropertyChanged(nameof(CanMacroDe));
        OnPropertyChanged(nameof(CanMacroDb));
        OnPropertyChanged(nameof(CanMacroRDb));
        OnPropertyChanged(nameof(CanMacroRr73));
        OnPropertyChanged(nameof(CanMacro73));
        CurrentTxMessage = _engine.Sequencer.Message ?? CurrentTxMessage;
    }

    private void RefreshDeviceLists()
    {
        InputDevices.Clear();
        OutputDevices.Clear();
        foreach (var device in _audio.ListInputDevices())
            InputDevices.Add(device);
        foreach (var device in _audio.ListOutputDevices())
            OutputDevices.Add(device);

        if (string.IsNullOrWhiteSpace(SelectedInputDeviceId) && InputDevices.Count > 0)
            SelectedInputDeviceId = InputDevices[0].Id;
        if (string.IsNullOrWhiteSpace(SelectedOutputDeviceId) && OutputDevices.Count > 0)
            SelectedOutputDeviceId = OutputDevices[0].Id;
    }

    private Ft4Settings BuildSettings()
    {
        var ft4 = _settings.Current.Ft4;
        var station = _settings.Current.GroundStation;
        return new Ft4Settings
        {
            InputDeviceId = SelectedInputDeviceId,
            OutputDeviceId = SelectedOutputDeviceId,
            Callsign = string.IsNullOrWhiteSpace(ft4.Callsign) ? "NOCALL" : ft4.Callsign,
            GridSquare = string.IsNullOrWhiteSpace(ft4.GridSquare) ? station.GridSquare : ft4.GridSquare,
            RxAudioFrequencyHz = RxFrequencyHz,
            TxAudioFrequencyHz = TxFrequencyHz,
            CutoffFrequencyHz = ft4.CutoffFrequencyHz,
            TxOdd = TxOdd,
            TxGain = TxGain,
        };
    }

    private void PersistSettings()
    {
        var ft4 = _settings.Current.Ft4;
        ft4.Callsign = Callsign;
        ft4.GridSquare = GridSquare;
        ft4.InputDeviceId = SelectedInputDeviceId;
        ft4.OutputDeviceId = SelectedOutputDeviceId;
        ft4.RxAudioFrequencyHz = RxFrequencyHz;
        ft4.TxAudioFrequencyHz = TxFrequencyHz;
        ft4.TxOdd = TxOdd;
        ft4.TxGain = TxGain;
        _ = _settings.SaveAsync();
    }
}

public sealed class Ft4MessageListItem
{
    public required Ft4DecodeLine Line { get; init; }
    public string DisplayText { get; init; } = "";
    public bool IsTransmit { get; init; }

    public static Ft4MessageListItem FromLine(Ft4DecodeLine line)
    {
        var text = line.IsTransmit
            ? $"TX  {line.Message}"
            : string.IsNullOrWhiteSpace(line.RawText)
                ? $"{line.Snr,3} {line.OffsetTimeSeconds,4:F1} {line.FrequencyHz,4} ~  {line.Message}"
                : line.RawText;

        return new Ft4MessageListItem
        {
            Line = line,
            DisplayText = text.Trim(),
            IsTransmit = line.IsTransmit,
        };
    }
}

public interface Ft4WaterfallTarget
{
    void PushRow(byte[] row);
}
