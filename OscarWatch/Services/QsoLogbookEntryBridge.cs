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
/// the logbook is asked to open. The CW keyer also reads the attached entry for macros.
/// </summary>
public sealed class QsoLogbookEntryBridge
{
    private QsoLogbookViewModel? _target;
    private string? _pendingCall;
    private string? _pendingGrid;

    /// <summary>Raised when a fill arrives with no logbook open, or when the keyer asks to open it.</summary>
    public event EventHandler? OpenLogbookRequested;

    /// <summary>The open logbook entry form, or null when the logbook window is closed.</summary>
    public QsoLogbookViewModel? Attached => _target;

    /// <summary>Entry fields for CW macros when a logbook is open.</summary>
    public ICwKeyerEntrySource? KeyerEntry => _keyerEntry ?? _target;

    private ICwKeyerEntrySource? _keyerEntry;

    /// <summary>Test hook: supply macro fields without a full logbook window.</summary>
    public void AttachKeyerEntry(ICwKeyerEntrySource entry) => _keyerEntry = entry;

    /// <summary>Test hook: clear a keyer-only entry source.</summary>
    public void DetachKeyerEntry(ICwKeyerEntrySource entry)
    {
        if (ReferenceEquals(_keyerEntry, entry))
            _keyerEntry = null;
    }

    public QsoLogbookFillResult Fill(string? call, string? grid)
    {
        if (_target is { } target)
            return target.FillFromDecode(call, grid) ? QsoLogbookFillResult.Filled : QsoLogbookFillResult.BusyEditing;

        if (!string.IsNullOrWhiteSpace(call))
            _pendingCall = call;
        if (!string.IsNullOrWhiteSpace(grid))
            _pendingGrid = grid;
        RequestOpen();
        return QsoLogbookFillResult.OpeningLogbook;
    }

    /// <summary>Ask the main window to open the logbook (no pending fill).</summary>
    public void RequestOpen() => OpenLogbookRequested?.Invoke(this, EventArgs.Empty);

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
