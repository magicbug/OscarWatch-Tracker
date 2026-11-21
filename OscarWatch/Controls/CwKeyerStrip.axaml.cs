using Avalonia;
using Avalonia.Controls;

namespace OscarWatch.Controls;

/// <summary>
/// Shared F1 to F12 CW keyer strip for the logbook and the CW decoder.
/// DataContext is a <see cref="ViewModels.CwKeyerPanelViewModel"/>.
/// </summary>
public partial class CwKeyerStrip : UserControl
{
    public static readonly StyledProperty<bool> ShowEditMessagesProperty =
        AvaloniaProperty.Register<CwKeyerStrip, bool>(nameof(ShowEditMessages));

    public static readonly StyledProperty<bool> ShowStatusProperty =
        AvaloniaProperty.Register<CwKeyerStrip, bool>(nameof(ShowStatus));

    public CwKeyerStrip()
    {
        InitializeComponent();
    }

    /// <summary>Shows the Edit messages button (used on the decoder).</summary>
    public bool ShowEditMessages
    {
        get => GetValue(ShowEditMessagesProperty);
        set => SetValue(ShowEditMessagesProperty, value);
    }

    /// <summary>Shows the keyer status line under the buttons (used on the decoder).</summary>
    public bool ShowStatus
    {
        get => GetValue(ShowStatusProperty);
        set => SetValue(ShowStatusProperty, value);
    }
}
