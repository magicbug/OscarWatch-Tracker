using OscarWatch.ViewModels;

namespace OscarWatch.Services;

public enum QsoLogbookFillResult
{
    /// <summary>The open logbook's entry form now holds the call or locator.</summary>
    Filled,

    /// <summary>The logbook was closed; it is opening and will take the call or locator when ready.</summary>
    OpeningLogbook,

    /// <summary>The logbook is editing a saved QSO, so nothing was changed.</summary>
    BusyEditing,
}

/// <summary>
/// Lets decoder windows put a callsign or locator into the logbook's entry form without
/// logging anything. The open logbook attaches itself; with none open, the fill waits and
/// the logbook is asked to open.
/// </summary>
public sealed class QsoLogbookEntryBridge
{
    private QsoLogbookViewModel? _target;
    private string? _pendingCall;
    private string? _pendingGrid;

    /// <summary>Raised when a fill arrives with no logbook open.</summary>
    public event EventHandler? OpenLogbookRequested;

    public QsoLogbookFillResult Fill(string? call, string? grid)
    {
        if (_target is { } target)
            return target.FillFromDecode(call, grid) ? QsoLogbookFillResult.Filled : QsoLogbookFillResult.BusyEditing;

        if (!string.IsNullOrWhiteSpace(call))
            _pendingCall = call;
        if (!string.IsNullOrWhiteSpace(grid))
            _pendingGrid = grid;
        OpenLogbookRequested?.Invoke(this, EventArgs.Empty);
        return QsoLogbookFillResult.OpeningLogbook;
    }

    /// <summary>Called by the logbook once it has loaded; applies any fill that was waiting.</summary>
    public void Attach(QsoLogbookViewModel logbook)
    {
        _target = logbook;
        if (_pendingCall is null && _pendingGrid is null)
            return;

        var (call, grid) = (_pendingCall, _pendingGrid);
        _pendingCall = null;
        _pendingGrid = null;
        logbook.FillFromDecode(call, grid);
    }

    public void Detach(QsoLogbookViewModel logbook)
    {
        if (ReferenceEquals(_target, logbook))
            _target = null;
    }
}
