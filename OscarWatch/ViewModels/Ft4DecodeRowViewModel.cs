using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.ViewModels;

/// <summary>One decode list row, with a background that tracks the current QSO partner.</summary>
public sealed partial class Ft4DecodeRowViewModel : ObservableObject
{
    public Ft4DecodeRowViewModel(
        Ft4DecodedMessage message,
        bool dtWsjtxConvention = true,
        double slotSeconds = Ft4SlotClock.Ft4SlotSeconds)
    {
        Message = message;
        _dtWsjtxConvention = dtWsjtxConvention;
        _dtText = FormatDt(message, dtWsjtxConvention);
        IsEvenSlot = Ft4SlotClock.IsEvenSlot(message.SlotUtc, slotSeconds);
    }

    public Ft4DecodedMessage Message { get; }

    /// <summary>True when this decode's slot is an even FT4 period (from <see cref="Ft4SlotClock"/>).</summary>
    public bool IsEvenSlot { get; }

    /// <summary>First Band activity row of this slot; drives the Even/Odd header in that list.</summary>
    [ObservableProperty] private bool _startsBandSlot;

    /// <summary>First Calling me row of this slot; drives the Even/Odd header in that list.</summary>
    [ObservableProperty] private bool _startsCallsToMeSlot;

    private bool _dtWsjtxConvention;

    /// <summary>
    /// DT column text (RX decodes and own echoes). Follows the WSJT-X convention toggle,
    /// which only changes the displayed value, not the stored DT.
    /// </summary>
    [ObservableProperty] private string _dtText;

    /// <summary>Updates the DT column when the WSJT-X convention setting changes.</summary>
    public void SetDtConvention(bool wsjtxConvention)
    {
        if (_dtWsjtxConvention == wsjtxConvention)
            return;
        _dtWsjtxConvention = wsjtxConvention;
        DtText = FormatDt(Message, wsjtxConvention);
    }

    private static string FormatDt(Ft4DecodedMessage message, bool wsjtxConvention) =>
        message.DisplayDtSec(wsjtxConvention).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;

    /// <summary>Set for a shaded receive line when the operator chose a text colour. Otherwise the row keeps the theme text.</summary>
    [ObservableProperty] private IBrush? _rowForeground;

    /// <summary>Colour of the TX label and the message on a line this station sent.</summary>
    [ObservableProperty] private IBrush _txForeground = Brushes.White;

    /// <summary>The line comes from the armed wait-and-pounce target. Shown as a text tag, not only colour.</summary>
    [ObservableProperty] private bool _isPounceTarget;

    public bool HasRowForeground => RowForeground is not null;

    partial void OnRowForegroundChanged(IBrush? value) => OnPropertyChanged(nameof(HasRowForeground));

    private string? _appliedColourHex;
    private string? _appliedTextHex;
    private string? _appliedTxHex;
    private bool _highlightApplied;

    public void RefreshHighlight(
        string? myCall,
        string? partnerCall,
        string callingMeColour,
        string replyingColour,
        string newCallColour,
        string newGridColour,
        string cqColour,
        IReadOnlySet<string>? workedCalls,
        IReadOnlySet<string>? workedGridFields,
        IReadOnlySet<string>? finishedPartners = null,
        string? callingMeText = null,
        string? replyingText = null,
        string? newCallText = null,
        string? newGridText = null,
        string? cqText = null,
        string? txText = null,
        Ft4PounceTarget? pounceTarget = null)
    {
        var kind = Ft4DecodeHighlight.Classify(
            Message,
            myCall,
            partnerCall,
            workedCalls,
            workedGridFields,
            finishedPartners,
            pounceTarget);
        IsPounceTarget = kind == Ft4DecodeHighlightKind.PounceTarget && !Message.IsApriori;
        var hex = kind switch
        {
            Ft4DecodeHighlightKind.PounceTarget => Ft4DecodeHighlight.DefaultPounceColour,
            Ft4DecodeHighlightKind.Replying => replyingColour,
            Ft4DecodeHighlightKind.CallingMe => callingMeColour,
            Ft4DecodeHighlightKind.NewCall => newCallColour,
            Ft4DecodeHighlightKind.NewGrid => newGridColour,
            Ft4DecodeHighlightKind.Cq => cqColour,
            _ => null
        };
        var textHex = kind switch
        {
            Ft4DecodeHighlightKind.Replying => Ft4DecodeHighlight.NormalizeColour(replyingText),
            Ft4DecodeHighlightKind.CallingMe => Ft4DecodeHighlight.NormalizeColour(callingMeText),
            Ft4DecodeHighlightKind.NewCall => Ft4DecodeHighlight.NormalizeColour(newCallText),
            Ft4DecodeHighlightKind.NewGrid => Ft4DecodeHighlight.NormalizeColour(newGridText),
            Ft4DecodeHighlightKind.Cq => Ft4DecodeHighlight.NormalizeColour(cqText),
            _ => null
        };
        var txHex = Ft4DecodeHighlight.NormalizeColour(txText) ?? Ft4DecodeHighlight.DefaultTxTextColour;

        // Replacing the brush when the colour has not changed repaints every row.
        if (_highlightApplied
            && string.Equals(_appliedColourHex, hex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_appliedTextHex, textHex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_appliedTxHex, txHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _highlightApplied = true;
        _appliedColourHex = hex;
        _appliedTextHex = textHex;
        _appliedTxHex = txHex;
        RowBackground = Ft4DecodeRowBackgroundConverter.BrushFromHex(hex);
        RowForeground = textHex is null ? null : Ft4DecodeRowBackgroundConverter.BrushFromHex(textHex);
        TxForeground = Ft4DecodeRowBackgroundConverter.BrushFromHex(txHex);
    }
}
