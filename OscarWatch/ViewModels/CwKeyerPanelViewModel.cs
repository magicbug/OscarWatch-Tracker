using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Cw;
using OscarWatch.Hardware;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Views;

namespace OscarWatch.ViewModels;

/// <summary>Which window is showing the shared CW keyer strip.</summary>
public enum CwKeyerHost
{
    Logbook,
    Decoder
}

/// <summary>
/// Shared CW keyer strip for the logbook and the CW decoder. One message at a time through
/// <see cref="CwKeyerService"/>; both windows bind the same instance.
/// </summary>
public sealed partial class CwKeyerPanelViewModel : ObservableObject
{
    private readonly CwKeyerService _cwKeyer;
    private readonly ISettingsService _settings;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly QsoLogbookEntryBridge _entryBridge;
    private readonly ILocalizationService _l;
    private readonly CwUplinkAlignService? _uplinkAlign;
    private readonly HashSet<CwKeyerHost> _hosts = [];
    private bool _suppressCwWpmSave;
    private CwKeyboardViewModel? _keyboard;
    private CwKeyboardWindow? _keyboardWindow;
    private CwAlignRefusalReason _alignRefusal = CwAlignRefusalReason.NotLinearTransponder;
    private string _lastAlignStatus = "";

    public CwKeyerPanelViewModel(
        CwKeyerService cwKeyer,
        ISettingsService settings,
        ILiveTrackerSnapshotProvider tracker,
        QsoLogbookEntryBridge entryBridge,
        ILocalizationService localization,
        CwUplinkAlignService? uplinkAlign = null)
    {
        _cwKeyer = cwKeyer;
        _settings = settings;
        _tracker = tracker;
        _entryBridge = entryBridge;
        _l = localization;
        _uplinkAlign = uplinkAlign;

        for (var i = 0; i < CwKeyerSettings.MessageCount; i++)
            CwButtons.Add(new CwMacroButtonViewModel(i, SendCwMacro));

        _cwKeyer.Finished += OnCwKeyerFinished;
        if (_uplinkAlign is not null)
            _uplinkAlign.StateChanged += OnUplinkAlignStateChanged;
        Reload();
    }

    public ObservableCollection<CwMacroButtonViewModel> CwButtons { get; } = [];

    public int CwMinWpm => CwKeyerSettings.MinWpm;

    public int CwMaxWpm => CwKeyerSettings.MaxWpm;

    [ObservableProperty]
    private bool _showCwKeyer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCwCommand))]
    [NotifyCanExecuteChangedFor(nameof(AlignUplinkCommand))]
    private bool _cwSending;

    [ObservableProperty]
    private decimal? _cwWpm = CwKeyerSettings.DefaultWpm;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AlignUplinkCommand))]
    private bool _keyboardOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AlignUplinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCwCommand))]
    private bool _isAligning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAlignCommand))]
    private bool _canUndoAlign;

    [ObservableProperty]
    private string _alignButtonToolTip = "";

    [ObservableProperty]
    private string _undoAlignToolTip = "";

    /// <summary>Raised when the operator asks to edit messages and a logbook is already open.</summary>
    public event EventHandler? EditMessagesRequested;

    /// <summary>A window started showing the strip.</summary>
    public void AttachHost(CwKeyerHost host)
    {
        _hosts.Add(host);
        Refresh(_tracker.GetCurrent());
    }

    /// <summary>
    /// A window stopped showing the strip. The last host stops any send and releases the key port.
    /// </summary>
    public void DetachHost(CwKeyerHost host)
    {
        _hosts.Remove(host);
        if (_hosts.Count > 0)
            return;

        CloseKeyboard();
        if (IsAligning)
            _uplinkAlign?.Abort();
        if (CwSending)
            StopCw();
        else
            _cwKeyer.ReleasePort();
    }

    /// <summary>Re-read the CW keyer settings (after the Logbook settings dialogue closes).</summary>
    public void Reload()
    {
        var cw = _settings.Current.QsoLogbook.CwKeyer;
        cw.Normalise();
        for (var i = 0; i < CwButtons.Count; i++)
            CwButtons[i].Load(cw.Messages[i]);

        _suppressCwWpmSave = true;
        CwWpm = cw.Wpm;
        _suppressCwWpmSave = false;
        _cwKeyer.SetWpm(cw.Wpm);
        _cwKeyer.ReleaseUnusedPort();
        Refresh(_tracker.GetCurrent());
    }

    /// <summary>Update visibility and button tooltips from the live station mode and entry form.</summary>
    public void Refresh(LiveTrackerSnapshot snapshot)
    {
        _uplinkAlign?.CheckRuntimeGuards(snapshot);

        var cw = _settings.Current.QsoLogbook.CwKeyer;
        ShowCwKeyer = cw.Enabled && IsCwMode(snapshot.Mode);
        if (!ShowCwKeyer)
            return;

        var context = BuildCwContext();
        foreach (var button in CwButtons)
            button.RefreshToolTip(context, _l);

        RefreshAlignState(snapshot);
    }

    /// <summary>F1 to F12 from a host window. Returns true when the key was used.</summary>
    public bool TryHandleCwFunctionKey(int slot)
    {
        if (!ShowCwKeyer || slot < 0 || slot >= CwButtons.Count)
            return false;

        if (KeyboardOpen && _keyboard is not null)
        {
            AppendMacroToKeyboard(slot);
            return true;
        }

        SendCwMacro(slot);
        return true;
    }

    /// <summary>Escape from a host window. Returns true when it stopped a message.</summary>
    public bool TryHandleCwEscape()
    {
        if (KeyboardOpen && _keyboard is not null)
        {
            if (!_cwKeyer.IsSending && string.IsNullOrEmpty(_keyboard.PendingText)
                && string.IsNullOrEmpty(_keyboard.DraftText))
                return false;

            _keyboard.Abort();
            CwSending = false;
            SetActiveCwSlot(-1);
            StatusText = _l.Get("Logbook.Cw.Status.Stopped");
            return true;
        }

        if (IsAligning)
        {
            StopCw();
            return true;
        }

        if (!CwSending)
            return false;

        StopCw();
        return true;
    }

    /// <summary>Ctrl+K from a host window. Returns true when the key was used.</summary>
    public bool TryHandleCtrlK(Window owner)
    {
        if (!ShowCwKeyer)
            return false;

        ToggleKeyboard(owner);
        return true;
    }

    private bool CanStopCw() => CwSending || IsAligning;

    [RelayCommand(CanExecute = nameof(CanStopCw))]
    private void StopCw()
    {
        if (IsAligning && _uplinkAlign is not null)
        {
            _uplinkAlign.Abort(_l.Get("Logbook.Cw.Align.Status.Aborted"));
            CwSending = _cwKeyer.IsSending;
            SetActiveCwSlot(-1);
            return;
        }

        if (KeyboardOpen && _keyboard is not null)
        {
            _keyboard.Abort();
            CwSending = false;
            SetActiveCwSlot(-1);
            StatusText = _l.Get("Logbook.Cw.Status.Stopped");
            return;
        }

        _cwKeyer.Stop();
        CwSending = false;
        SetActiveCwSlot(-1);
        StatusText = _l.Get("Logbook.Cw.Status.Stopped");
    }

    private bool CanAlignUplink() =>
        _uplinkAlign is not null
        && !IsAligning
        && !CwSending
        && !KeyboardOpen
        && _alignRefusal == CwAlignRefusalReason.None;

    [RelayCommand(CanExecute = nameof(CanAlignUplink))]
    private void AlignUplink()
    {
        if (_uplinkAlign is null)
            return;

        SetActiveCwSlot(-1);
        _uplinkAlign.Start();
    }

    private bool CanExecuteUndoAlign() => CanUndoAlign;

    [RelayCommand(CanExecute = nameof(CanExecuteUndoAlign))]
    private void UndoAlign()
    {
        _uplinkAlign?.Undo();
    }

    [RelayCommand]
    private void EditMessages()
    {
        if (_entryBridge.Attached is null)
        {
            StatusText = _l.Get("Logbook.Cw.Status.OpenLogbookToEdit");
            _entryBridge.RequestOpen();
            return;
        }

        EditMessagesRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Open or close the keyboard from the strip button.</summary>
    public void ToggleKeyboardFromStrip(Window owner)
    {
        if (!ShowCwKeyer)
            return;

        ToggleKeyboard(owner);
    }

    /// <summary>Test helper: attach a keyboard view model without opening a window.</summary>
    internal void AttachKeyboardForTests(CwKeyboardViewModel keyboard)
    {
        _keyboard = keyboard;
        KeyboardOpen = true;
        CwSending = _cwKeyer.IsSending;
        SetActiveCwSlot(-1);
        StatusText = _l.Get("Logbook.Cw.Status.Keyboard");
    }

    /// <summary>Test helper: clear the keyboard view model without closing a window.</summary>
    internal void DetachKeyboardForTests()
    {
        if (_keyboard is not null)
        {
            _keyboard.Dispose();
            _keyboard = null;
        }

        KeyboardOpen = false;
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
        _cwKeyer.SetWpm(wpm);
    }

    private void ToggleKeyboard(Window owner)
    {
        if (KeyboardOpen)
        {
            CloseKeyboard();
            return;
        }

        OpenKeyboard(owner);
    }

    private void OpenKeyboard(Window owner)
    {
        if (IsAligning)
            _uplinkAlign?.Abort();

        if (_keyboardWindow is not null)
        {
            _keyboardWindow.Activate();
            return;
        }

        _keyboard = new CwKeyboardViewModel(_cwKeyer, BuildCwContext, _l);
        _keyboard.CloseRequested += OnKeyboardCloseRequested;
        _keyboard.SaveQsoRequested += OnKeyboardSaveQsoRequested;
        _keyboard.SessionActive += OnKeyboardSessionActive;

        var result = _keyboard.EnsureSession();
        if (result != CwKeyerStartResult.Started)
        {
            StatusText = DescribeCwStartFailure(result);
            _keyboard.Dispose();
            _keyboard = null;
            return;
        }

        CwSending = true;
        SetActiveCwSlot(-1);
        StatusText = _l.Get("Logbook.Cw.Status.Keyboard");
        KeyboardOpen = true;

        var window = new CwKeyboardWindow { DataContext = _keyboard };
        window.Closed += OnKeyboardWindowClosed;
        _keyboardWindow = window;
        window.Show(owner);
    }

    private void CloseKeyboard()
    {
        if (_keyboardWindow is null && _keyboard is null)
        {
            KeyboardOpen = false;
            return;
        }

        var window = _keyboardWindow;
        _keyboardWindow = null;
        if (window is not null)
        {
            window.Closed -= OnKeyboardWindowClosed;
            window.Close();
        }

        DisposeKeyboard();
        if (_cwKeyer.IsKeyboardSession)
            _cwKeyer.Stop();

        KeyboardOpen = false;
        if (!_cwKeyer.IsSending)
        {
            CwSending = false;
            SetActiveCwSlot(-1);
        }
    }

    private void OnKeyboardCloseRequested(object? sender, EventArgs e) => CloseKeyboard();

    private void OnKeyboardSessionActive(object? sender, EventArgs e)
    {
        CwSending = true;
        SetActiveCwSlot(-1);
        StatusText = _l.Get("Logbook.Cw.Status.Keyboard");
    }

    private void OnKeyboardWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _keyboardWindow) && _keyboardWindow is not null)
            return;

        _keyboardWindow = null;
        DisposeKeyboard();
        if (_cwKeyer.IsKeyboardSession)
            _cwKeyer.Stop();

        KeyboardOpen = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_cwKeyer.IsSending)
            {
                CwSending = false;
                SetActiveCwSlot(-1);
                if (string.IsNullOrEmpty(StatusText) || StatusText == _l.Get("Logbook.Cw.Status.Keyboard"))
                    StatusText = _l.Get("Logbook.Cw.Status.Stopped");
            }
        });
    }

    private void DisposeKeyboard()
    {
        if (_keyboard is null)
            return;

        _keyboard.CloseRequested -= OnKeyboardCloseRequested;
        _keyboard.SaveQsoRequested -= OnKeyboardSaveQsoRequested;
        _keyboard.SessionActive -= OnKeyboardSessionActive;
        _keyboard.Dispose();
        _keyboard = null;
    }

    private void OnKeyboardSaveQsoRequested(object? sender, EventArgs e) =>
        _ = SaveQsoFromCwAsync(showResult: false);

    private void AppendMacroToKeyboard(int slot)
    {
        if (_keyboard is null || slot < 0 || slot >= CwButtons.Count)
            return;

        var button = CwButtons[slot];
        var expansion = CwMacroExpander.Expand(button.Template, BuildCwContext());
        if (expansion.Message.IsEmpty && !expansion.SaveQso)
        {
            StatusText = _l.Get("Logbook.Cw.Status.Empty", button.KeyName);
            return;
        }

        if (!_keyboard.AppendMacro(expansion))
        {
            StatusText = DescribeCwStartFailure(CwKeyerStartResult.RigNotConnected);
            return;
        }

        CwSending = true;
        StatusText = expansion.UnknownTokens.Count == 0
            ? _l.Get("Logbook.Cw.Status.KeyboardSending", expansion.Message.DisplayText)
            : _l.Get(
                "Logbook.Cw.Status.KeyboardSendingUnknown",
                expansion.Message.DisplayText,
                string.Join(", ", expansion.UnknownTokens.Select(t => "{" + t + "}")));
    }

    private void SendCwMacro(int slot)
    {
        if (slot < 0 || slot >= CwButtons.Count)
            return;

        if (IsAligning)
            _uplinkAlign?.Abort();

        if (KeyboardOpen)
        {
            AppendMacroToKeyboard(slot);
            return;
        }

        var button = CwButtons[slot];
        var expansion = CwMacroExpander.Expand(button.Template, BuildCwContext());
        if (expansion.Message.IsEmpty)
        {
            if (expansion.SaveQso)
            {
                if (CwSending)
                    StopCw();
                _ = SaveQsoFromCwAsync(showResult: true);
            }
            else
                StatusText = _l.Get("Logbook.Cw.Status.Empty", button.KeyName);
            return;
        }

        var result = _cwKeyer.Send(slot, expansion.Message, expansion.SaveQso);
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
            if (e.Slot == CwKeyerService.ProbeSlot)
            {
                CwSending = _cwKeyer.IsSending;
                return;
            }

            // A newer message may already be running; only that one owns the strip state.
            var stillSending = _cwKeyer.IsSending;
            if (!stillSending && !KeyboardOpen)
            {
                CwSending = false;
                SetActiveCwSlot(-1);
            }
            else if (!stillSending && KeyboardOpen)
            {
                // Esc/Stop aborted the keyboard session; the window stays open for corrections.
                CwSending = false;
                SetActiveCwSlot(-1);
            }

            if (e.Finish == CwKeyerFinish.Completed && e.SaveQso)
            {
                _ = SaveQsoFromCwAsync(showResult: !stillSending && !KeyboardOpen);
                return;
            }

            if (stillSending || KeyboardOpen)
            {
                if (KeyboardOpen && !stillSending && e.Slot == CwKeyerService.KeyboardSlot)
                    StatusText = e.Finish switch
                    {
                        CwKeyerFinish.RigRejected => _l.Get("Logbook.Cw.Status.Rejected"),
                        CwKeyerFinish.Aborted => _l.Get("Logbook.Cw.Status.Stopped"),
                        _ => StatusText
                    };
                return;
            }

            var keyName = e.Slot >= 0 && e.Slot < CwButtons.Count ? CwButtons[e.Slot].KeyName : "";
            StatusText = e.Finish switch
            {
                CwKeyerFinish.Completed => _l.Get("Logbook.Cw.Status.Sent", keyName),
                CwKeyerFinish.RigRejected => _l.Get("Logbook.Cw.Status.Rejected"),
                _ => StatusText
            };
        });

    /// <summary>
    /// Logs the entry row after a message that contains {SAVEQSO} has finished.
    /// A message that is still going keeps its own status line.
    /// </summary>
    private async Task SaveQsoFromCwAsync(bool showResult)
    {
        var entry = _entryBridge.KeyerEntry;
        if (entry is null || !entry.CanCommitQso)
        {
            if (showResult && !CwSending)
                StatusText = _l.Get("Logbook.Cw.Status.NotLogged");
            return;
        }

        await entry.CommitQsoFromCwAsync().ConfigureAwait(true);
        if (CwSending)
            AnnounceActiveCw();
    }

    private void AnnounceActiveCw()
    {
        if (_cwKeyer.IsKeyboardSession)
        {
            StatusText = _l.Get("Logbook.Cw.Status.Keyboard");
            return;
        }

        var slot = _cwKeyer.ActiveSlot;
        if (slot < 0 || slot >= CwButtons.Count)
            return;

        var button = CwButtons[slot];
        var expansion = CwMacroExpander.Expand(button.Template, BuildCwContext());
        StatusText = _l.Get("Logbook.Cw.Status.Sending", button.KeyName, expansion.Message.DisplayText);
    }

    private void SetActiveCwSlot(int slot)
    {
        foreach (var button in CwButtons)
            button.IsActive = button.Slot == slot;
    }

    private CwMacroContext BuildCwContext()
    {
        if (_entryBridge.KeyerEntry is { } entry)
            return entry.BuildCwMacroContext();

        var station = _settings.Current.GroundStation;
        var myCall = station.Callsign?.Trim() ?? "";
        var myGrid = (station.GridSquare?.Trim() ?? "").ToUpperInvariant();

        return new CwMacroContext(
            MyCall: myCall,
            MyGrid: myGrid.Length >= 4 ? myGrid[..4] : myGrid,
            Satellite: _tracker.GetCurrent().SatelliteName);
    }

    private static bool IsCwMode(string? mode) =>
        string.Equals(mode?.Trim(), "CW", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode?.Trim(), "CW-R", StringComparison.OrdinalIgnoreCase);

    partial void OnShowCwKeyerChanged(bool value)
    {
        if (!value)
        {
            CloseKeyboard();
            if (IsAligning)
                _uplinkAlign?.Abort();
            if (CwSending)
                StopCw();
            _cwKeyer.ReleasePort();
        }
    }

    private void OnUplinkAlignStateChanged(object? sender, EventArgs e)
    {
        if (_uplinkAlign is null)
            return;

        IsAligning = _uplinkAlign.IsActive;
        CwSending = _cwKeyer.IsSending;
        CanUndoAlign = _uplinkAlign.CanUndo;

        var msg = _uplinkAlign.LastStatusMessage;
        if (!string.IsNullOrEmpty(msg) && !string.Equals(msg, _lastAlignStatus, StringComparison.Ordinal))
        {
            _lastAlignStatus = msg;
            if (!_cwKeyer.IsSending || _cwKeyer.IsProbeSession)
                StatusText = msg;
        }

        RefreshAlignState(_tracker.GetCurrent());
    }

    private void RefreshAlignState(LiveTrackerSnapshot snapshot)
    {
        if (_uplinkAlign is null)
        {
            _alignRefusal = CwAlignRefusalReason.NotLinearTransponder;
            IsAligning = false;
            CanUndoAlign = false;
            AlignButtonToolTip = "";
            UndoAlignToolTip = "";
            AlignUplinkCommand.NotifyCanExecuteChanged();
            return;
        }

        IsAligning = _uplinkAlign.IsActive;
        CanUndoAlign = _uplinkAlign.CanUndo;
        _alignRefusal = _uplinkAlign.CheckPrerequisites(snapshot);

        var targetPitch = CwKeyerSettings.ClampAlignTargetPitchHz(_settings.Current.QsoLogbook.CwKeyer.AlignTargetPitchHz);
        if (IsAligning)
        {
            AlignButtonToolTip = _uplinkAlign.LastStatusMessage;
        }
        else if (CwSending || KeyboardOpen)
        {
            AlignButtonToolTip = _l.Get("Logbook.Cw.Align.Refusal.Busy");
        }
        else if (_alignRefusal != CwAlignRefusalReason.None)
        {
            AlignButtonToolTip = _uplinkAlign.DescribeRefusal(_alignRefusal);
        }
        else
        {
            AlignButtonToolTip = _l.Get("Logbook.Cw.Align.Tip", targetPitch);
        }

        UndoAlignToolTip = _uplinkAlign.LastUndo is { } undo
            ? _l.Get("Logbook.Cw.Align.Undo.Tip", CwUplinkAlignService.FormatSignedHz(undo.AppliedDeltaHz))
            : "";

        AlignUplinkCommand.NotifyCanExecuteChanged();
        UndoAlignCommand.NotifyCanExecuteChanged();
        StopCwCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One of the twelve CW message buttons under the QSO entry or on the decoder.</summary>
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
        var text = expansion.Message.DisplayText;
        if (expansion.SaveQso)
            text = string.IsNullOrEmpty(text)
                ? l.Get("Logbook.Cw.SaveNote")
                : text + " " + l.Get("Logbook.Cw.SaveNote");
        ToolTip = l.Get("Logbook.Cw.ButtonToolTip", KeyName, text);
    }

    [RelayCommand(CanExecute = nameof(HasMessage))]
    private void Send() => _send(Slot);
}
