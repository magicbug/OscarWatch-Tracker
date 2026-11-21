using System.ComponentModel;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;

namespace OscarWatch.ViewModels;

public partial class QsoLogbookViewModel
{
    private CwKeyerPanelViewModel? _cwKeyer;
    private bool _cwKeyerListening;

    /// <summary>Shared CW keyer strip. Null in tests that omit the panel.</summary>
    public CwKeyerPanelViewModel? CwKeyer => _cwKeyer;

    private void InitializeCwKeyer(CwKeyerPanelViewModel? cwKeyer)
    {
        _cwKeyer = cwKeyer;
        if (_cwKeyer is null)
            return;

        _cwKeyer.PropertyChanged += OnCwKeyerPanelPropertyChanged;
        _cwKeyerListening = true;
        _cwKeyer.AttachHost(CwKeyerHost.Logbook);
    }

    /// <summary>Re-read the CW keyer settings (after the Logbook settings dialogue closes).</summary>
    public void ReloadCwKeyer() => _cwKeyer?.Reload();

    /// <summary>F1 to F12 from the window. Returns true when the key was used.</summary>
    public bool TryHandleCwFunctionKey(int slot) =>
        _cwKeyer?.TryHandleCwFunctionKey(slot) ?? false;

    /// <summary>Escape from the window. Returns true when it stopped a message.</summary>
    public bool TryHandleCwEscape() =>
        _cwKeyer?.TryHandleCwEscape() ?? false;

    /// <summary>Values for CW macros from the entry form and the station call / grid.</summary>
    public CwMacroContext BuildCwMacroContext()
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

    /// <summary>Logs the current entry after a message that contained {SAVEQSO}.</summary>
    public Task CommitQsoFromCwAsync() => CommitQsoAsync();

    private void RefreshCwKeyer(LiveTrackerSnapshot snapshot) =>
        _cwKeyer?.Refresh(snapshot);

    private void OnCwKeyerPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_cwKeyer is null)
            return;

        if (e.PropertyName == nameof(CwKeyerPanelViewModel.StatusText)
            && _cwKeyer.ShowCwKeyer
            && !string.IsNullOrEmpty(_cwKeyer.StatusText))
        {
            StatusText = _cwKeyer.StatusText;
        }
    }

    private void DisposeCwKeyer()
    {
        if (_cwKeyer is null)
            return;

        if (_cwKeyerListening)
        {
            _cwKeyer.PropertyChanged -= OnCwKeyerPanelPropertyChanged;
            _cwKeyerListening = false;
        }

        _cwKeyer.DetachHost(CwKeyerHost.Logbook);
        _cwKeyer = null;
    }
}
