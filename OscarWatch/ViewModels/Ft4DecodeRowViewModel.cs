using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.ViewModels;

/// <summary>One decode list row, with a background that tracks the current QSO partner.</summary>
public sealed partial class Ft4DecodeRowViewModel : ObservableObject
{
    public Ft4DecodeRowViewModel(Ft4DecodedMessage message)
    {
        Message = message;
    }

    public Ft4DecodedMessage Message { get; }

    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;

    public void RefreshHighlight(
        string? myCall,
        string? partnerCall,
        string callingMeColour,
        string replyingColour)
    {
        var kind = Ft4DecodeHighlight.Classify(Message, myCall, partnerCall);
        var hex = kind switch
        {
            Ft4DecodeHighlightKind.Replying => replyingColour,
            Ft4DecodeHighlightKind.CallingMe => callingMeColour,
            _ => null
        };
        RowBackground = Ft4DecodeRowBackgroundConverter.BrushFromHex(hex);
    }
}
