using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Hardware;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class QsoLogbookViewModel
{
    private CwKeyerService? _cwKeyer;
    private bool _suppressCwWpmSave;

    public ObservableCollection<CwMacroButtonViewModel> CwButtons { get; } = [];

    public int CwMinWpm => CwKeyerSettings.MinWpm;

    public int CwMaxWpm => CwKeyerSettings.MaxWpm;

    [ObservableProperty]
    private bool _showCwKeyer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCwCommand))]
    private bool _cwSending;

    [ObservableProperty]
    private decimal? _cwWpm = CwKeyerSettings.DefaultWpm;

    private void InitializeCwKeyer(CwKeyerService? cwKeyer)
    {
        _cwKeyer = cwKeyer;
        for (var i = 0; i < CwKeyerSettings.MessageCount; i++)
            CwButtons.Add(new CwMacroButtonViewModel(i, SendCwMacro));

        if (_cwKeyer is not null)
            _cwKeyer.Finished += OnCwKeyerFinished;

        ReloadCwKeyer();
    }

    /// <summary>Re-read the CW keyer settings (after the Logbook settings dialogue closes).</summary>
    public void ReloadCwKeyer()
    {
        var cw = _settings.Current.QsoLogbook.CwKeyer;
        cw.Normalise();
        for (var i = 0; i < CwButtons.Count; i++)
            CwButtons[i].Load(cw.Messages[i]);

        _suppressCwWpmSave = true;
        CwWpm = cw.Wpm;
        _suppressCwWpmSave = false;
        _cwKeyer?.SetWpm(cw.Wpm);
        RefreshCwKeyer(_tracker.GetCurrent());
    }

    /// <summary>F1 to F12 from the window. Returns true when the key was used.</summary>
    public bool TryHandleCwFunctionKey(int slot)
    {
        if (!ShowCwKeyer || slot < 0 || slot >= CwButtons.Count)
            return false;

        SendCwMacro(slot);
        return true;
    }

    /// <summary>Escape from the window. Returns true when it stopped a message.</summary>
    public bool TryHandleCwEscape()
    {
        if (!CwSending)
            return false;

        StopCw();
        return true;
    }

    private bool CanStopCw() => CwSending;

    [RelayCommand(CanExecute = nameof(CanStopCw))]
    private void StopCw()
    {
        _cwKeyer?.Stop();
        CwSending = false;
        SetActiveCwSlot(-1);
        StatusText = _l.Get("Logbook.Cw.Status.Stopped");
    }

    partial void OnCwWpmChanged(decimal? value)
    {
        var cw = _settings.Current.QsoLogbook.CwKeyer;
        if (value is not { } typed)
        {
            CwWpm = cw.Wpm;
            return;
        }

        var wpm = CwKeyerSettings.ClampWpm((int)Math.Round(typed));
        if (wpm != typed)
        {
            CwWpm = wpm;
            return;
        }

        if (_suppressCwWpmSave)
            return;

        if (cw.Wpm == wpm)
            return;

        cw.Wpm = wpm;
        _settings.RequestSave();
        _cwKeyer?.SetWpm(wpm);
    }

    private void SendCwMacro(int slot)
    {
        if (_cwKeyer is null || slot < 0 || slot >= CwButtons.Count)
            return;

        var button = CwButtons[slot];
        var expansion = CwMacroExpander.Expand(button.Template, BuildCwContext());
        if (expansion.Message.IsEmpty)
        {
            StatusText = _l.Get("Logbook.Cw.Status.Empty", button.KeyName);
            return;
        }

        var result = _cwKeyer.Send(slot, expansion.Message);
        if (result != CwKeyerStartResult.Started)
        {
            CwSending = false;
            SetActiveCwSlot(-1);
            StatusText = DescribeCwStartFailure(result);
            return;
        }

        CwSending = true;
        SetActiveCwSlot(slot);
        StatusText = expansion.UnknownTokens.Count == 0
            ? _l.Get("Logbook.Cw.Status.Sending", button.KeyName, expansion.Message.DisplayText)
            : _l.Get(
                "Logbook.Cw.Status.SendingUnknown",
                button.KeyName,
                expansion.Message.DisplayText,
                string.Join(", ", expansion.UnknownTokens.Select(t => "{" + t + "}")));
    }

    private string DescribeCwStartFailure(CwKeyerStartResult result)
    {
        var port = _settings.Current.QsoLogbook.CwKeyer.SeparatePort?.Trim() ?? "";
        return result switch
        {
            CwKeyerStartResult.NothingToSend => _l.Get("Logbook.Cw.Status.Empty", ""),
            CwKeyerStartResult.RigNotConnected => _l.Get("Logbook.Cw.Status.RigNotConnected"),
            CwKeyerStartResult.KeyLineUnavailable => _l.Get("Logbook.Cw.Status.KeyLineUnavailable"),
            CwKeyerStartResult.RtsBlocked => _l.Get("Logbook.Cw.Status.RtsBlocked"),
            CwKeyerStartResult.NoPortConfigured => _l.Get("Logbook.Cw.Status.NoPort"),
            CwKeyerStartResult.PortConflict => _l.Get("Logbook.Cw.Status.PortConflict", port),
            CwKeyerStartResult.PortBusy => _l.Get("Logbook.Cw.Status.PortBusy", port),
            CwKeyerStartResult.PortNotFound => _l.Get("Logbook.Cw.Status.PortNotFound", port),
            _ => _l.Get("Logbook.Cw.Status.PortFailed", port)
        };
    }

    private void OnCwKeyerFinished(object? sender, CwKeyerFinishedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A newer message may already be running; only that one owns the strip state.
            if (_cwKeyer is null || _cwKeyer.ActiveSlot >= 0)
                return;

            CwSending = false;
            SetActiveCwSlot(-1);
            var keyName = e.Slot >= 0 && e.Slot < CwButtons.Count ? CwButtons[e.Slot].KeyName : "";
            StatusText = e.Finish switch
            {
                CwKeyerFinish.Completed => _l.Get("Logbook.Cw.Status.Sent", keyName),
                CwKeyerFinish.RigRejected => _l.Get("Logbook.Cw.Status.Rejected"),
                _ => StatusText
            };
        });

    private void SetActiveCwSlot(int slot)
    {
        foreach (var button in CwButtons)
            button.IsActive = button.Slot == slot;
    }

    private CwMacroContext BuildCwContext()
    {
        var station = _settings.Current.GroundStation;
        var myCall = FirstNonEmpty(SelectedLogbook?.MyCallsign, station.Callsign);
        var myGrid = FirstNonEmpty(SelectedLogbook?.MyGridSquare, station.GridSquare).ToUpperInvariant();

        return new CwMacroContext(
            MyCall: myCall,
            Call: Call.Trim(),
            RstSent: RstSent.Trim(),
            RstRcvd: RstRcvd.Trim(),
            Grid: Grid.Trim(),
            MyGrid: myGrid.Length >= 4 ? myGrid[..4] : myGrid,
            Name: Name.Trim(),
            Satellite: _tracker.GetCurrent().SatelliteName);
    }

    private void RefreshCwKeyer(LiveTrackerSnapshot snapshot)
    {
        var cw = _settings.Current.QsoLogbook.CwKeyer;
        ShowCwKeyer = _cwKeyer is not null && cw.Enabled && IsCwMode(snapshot.Mode);
        if (!ShowCwKeyer)
            return;

        var context = BuildCwContext();
        foreach (var button in CwButtons)
            button.RefreshToolTip(context, _l);
    }

    private static bool IsCwMode(string? mode) =>
        string.Equals(mode?.Trim(), "CW", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode?.Trim(), "CW-R", StringComparison.OrdinalIgnoreCase);

    partial void OnShowCwKeyerChanged(bool value)
    {
        if (!value && CwSending)
            StopCw();
    }

    private void DisposeCwKeyer()
    {
        if (_cwKeyer is null)
            return;

        _cwKeyer.Finished -= OnCwKeyerFinished;
        _cwKeyer.ReleasePort();
    }
}

/// <summary>One of the twelve CW message buttons under the QSO entry.</summary>
public sealed partial class CwMacroButtonViewModel : ObservableObject
{
    private readonly Action<int> _send;

    public CwMacroButtonViewModel(int slot, Action<int> send)
    {
        Slot = slot;
        _send = send;
        KeyName = "F" + (slot + 1);
    }

    public int Slot { get; }

    public string KeyName { get; }

    public string Template { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _hasMessage;

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _toolTip = "";

    [ObservableProperty]
    private bool _isActive;

    public void Load(CwKeyerMessage message)
    {
        Template = message.Text;
        Label = message.Label;
        HasMessage = !string.IsNullOrWhiteSpace(message.Text);
    }

    public void RefreshToolTip(CwMacroContext context, ILocalizationService l)
    {
        if (!HasMessage)
        {
            ToolTip = l.Get("Logbook.Cw.ButtonEmpty", KeyName);
            return;
        }

        var expansion = CwMacroExpander.Expand(Template, context);
        ToolTip = l.Get("Logbook.Cw.ButtonToolTip", KeyName, expansion.Message.DisplayText);
    }

    [RelayCommand(CanExecute = nameof(HasMessage))]
    private void Send() => _send(Slot);
}
