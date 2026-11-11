using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Help;
using OscarWatch.Rotator;

namespace OscarWatch.ViewModels;

public partial class LogbookSettingsViewModel
{
    private string _rememberedCwPort = "";

    public ObservableCollection<CwMessageRowViewModel> CwMessages { get; } = [];

    public ObservableCollection<string> CwPortOptions { get; } = [];

    public IReadOnlyList<string> CwKeyLineOptions { get; } = ["DTR", "RTS"];

    [ObservableProperty]
    private bool _cwEnabled;

    [ObservableProperty]
    private decimal? _cwWpm = CwKeyerSettings.DefaultWpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwShowLineOptions))]
    [NotifyPropertyChangedFor(nameof(CwRouteText))]
    private bool _cwUseComLineOnTextRadios;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwRouteText))]
    private int _cwKeyLineIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwRouteText))]
    private bool _cwUseSeparatePort;

    [ObservableProperty]
    private string? _cwSeparatePort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwShowWarning))]
    private string _cwWarningText = "";

    public bool CwShowWarning => !string.IsNullOrWhiteSpace(CwWarningText);

    private RigType CwUplinkType => CwKeyingRoute.UplinkRigType(_settings.Current.Rig);

    public string CwUplinkText =>
        CwUplinkType == RigType.None
            ? _l.Get("Logbook.Settings.Cw.NoRig")
            : _l.Get("Logbook.Settings.Cw.Uplink", DeviceIdentityNames.Radio(CwUplinkType));

    public bool CwIsKenwoodUplink => CwKeyingRoute.IsKenwoodTextRadio(CwUplinkType);

    public bool CwIsIcomTextUplink => CwKeyingRoute.IsIcomTextRadio(CwUplinkType);

    public bool CwShowLineOptions =>
        !CwIsKenwoodUplink && !(CwIsIcomTextUplink && !CwUseComLineOnTextRadios);

    public string CwRouteText
    {
        get
        {
            var line = CwKeyLineOptions[Math.Clamp(CwKeyLineIndex, 0, 1)];
            return CwKeyingRoute.Resolve(BuildCwDraft(), _settings.Current.Rig) switch
            {
                CwKeyingMethod.KenwoodText => _l.Get("Logbook.Settings.Cw.Route.Kenwood"),
                CwKeyingMethod.IcomText => _l.Get("Logbook.Settings.Cw.Route.Icom"),
                CwKeyingMethod.CatPortLine => _l.Get("Logbook.Settings.Cw.Route.CatLine", line),
                _ => _l.Get("Logbook.Settings.Cw.Route.SeparateLine", line)
            };
        }
    }

    private void LoadCwKeyer()
    {
        var cw = _settings.Current.QsoLogbook.CwKeyer;
        cw.Normalise();
        CwEnabled = cw.Enabled;
        CwWpm = cw.Wpm;
        CwUseComLineOnTextRadios = cw.UseComLineOnTextRadios;
        CwKeyLineIndex = cw.KeyLine == PttLine.Rts ? 1 : 0;
        CwUseSeparatePort = cw.UseSeparatePort;
        _rememberedCwPort = cw.SeparatePort?.Trim() ?? "";

        CwMessages.Clear();
        for (var i = 0; i < cw.Messages.Count; i++)
            CwMessages.Add(new CwMessageRowViewModel(i, cw.Messages[i].Label, cw.Messages[i].Text));

        RefreshCwPorts();
        OnPropertyChanged(nameof(CwUplinkText));
        OnPropertyChanged(nameof(CwIsKenwoodUplink));
        OnPropertyChanged(nameof(CwIsIcomTextUplink));
        OnPropertyChanged(nameof(CwShowLineOptions));
        OnPropertyChanged(nameof(CwRouteText));
    }

    private void SaveCwKeyer()
    {
        var draft = BuildCwDraft();
        draft.Normalise();
        _settings.Current.QsoLogbook.CwKeyer = draft;
    }

    private CwKeyerSettings BuildCwDraft() => new()
    {
        Enabled = CwEnabled,
        Wpm = CwKeyerSettings.ClampWpm((int)Math.Round(CwWpm ?? CwKeyerSettings.DefaultWpm)),
        KeyLine = CwKeyLineIndex == 1 ? PttLine.Rts : PttLine.Dtr,
        UseSeparatePort = CwUseSeparatePort,
        SeparatePort = CwSeparatePort?.Trim() ?? "",
        UseComLineOnTextRadios = CwUseComLineOnTextRadios,
        Messages = CwMessages
            .Select(m => new CwKeyerMessage { Label = m.Label, Text = m.Text })
            .ToList()
    };

    /// <summary>Lists COM ports, keeping the saved port even when it is not plugged in.</summary>
    [RelayCommand]
    private void RefreshCwPorts()
    {
        var saved = CwSeparatePort?.Trim() ?? "";
        if (saved.Length == 0)
            saved = _rememberedCwPort;
        CwPortOptions.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePorts(forceRefresh: true))
            CwPortOptions.Add(port);
        var listed = CwPortOptions.FirstOrDefault(port =>
            string.Equals(port, saved, StringComparison.OrdinalIgnoreCase));
        if (listed is null && saved.Length > 0)
        {
            CwPortOptions.Insert(0, saved);
            listed = saved;
        }

        CwSeparatePort = listed;
        RefreshCwWarning();
    }

    [RelayCommand]
    private static void OpenCwHelp() => HelpLauncher.TryOpenHelp("cw-keyer");

    partial void OnCwKeyLineIndexChanged(int value) => RefreshCwWarning();

    partial void OnCwUseSeparatePortChanged(bool value) => RefreshCwWarning();

    partial void OnCwSeparatePortChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _rememberedCwPort = value.Trim();
        RefreshCwWarning();
    }

    partial void OnCwUseComLineOnTextRadiosChanged(bool value) => RefreshCwWarning();

    private void RefreshCwWarning()
    {
        var draft = BuildCwDraft();
        var method = CwKeyingRoute.Resolve(draft, _settings.Current.Rig);
        if (CwKeyingRoute.CatPortRtsBlocked(draft, _settings.Current.Rig))
        {
            CwWarningText = _l.Get("Logbook.Cw.Status.RtsBlocked");
        }
        else if (method == CwKeyingMethod.SeparatePortLine && string.IsNullOrWhiteSpace(draft.SeparatePort))
        {
            CwWarningText = _l.Get("Logbook.Cw.Status.NoPort");
        }
        else if (method == CwKeyingMethod.SeparatePortLine
                 && SerialPortConflictHelper.IsCwKeyPortOccupied(_settings.Current, draft.SeparatePort))
        {
            CwWarningText = _l.Get("Logbook.Cw.Status.PortConflict", draft.SeparatePort);
        }
        else if (method == CwKeyingMethod.CatPortLine && CwUplinkType == RigType.None)
        {
            CwWarningText = _l.Get("Logbook.Cw.Status.RigNotConnected");
        }
        else
        {
            CwWarningText = "";
        }

        OnPropertyChanged(nameof(CwRouteText));
    }
}

/// <summary>One editable F-key message in the CW keyer settings tab.</summary>
public sealed partial class CwMessageRowViewModel : ObservableObject
{
    public CwMessageRowViewModel(int slot, string label, string text)
    {
        KeyName = "F" + (slot + 1);
        _label = label;
        _text = text;
    }

    public string KeyName { get; }

    public int MaxLabelLength => CwKeyerSettings.MaxLabelLength;

    public int MaxMessageLength => CwKeyerSettings.MaxMessageLength;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _text;
}
