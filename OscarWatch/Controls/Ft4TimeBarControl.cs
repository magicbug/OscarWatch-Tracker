using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OscarWatch.Controls;

public sealed class Ft4TimeBarControl : Control
{
    public static readonly StyledProperty<double> SlotProgressProperty =
        AvaloniaProperty.Register<Ft4TimeBarControl, double>(nameof(SlotProgress), 0);

    public static readonly StyledProperty<double> TxWindowProgressProperty =
        AvaloniaProperty.Register<Ft4TimeBarControl, double>(nameof(TxWindowProgress), 0);

    public static readonly StyledProperty<bool> IsOddSlotProperty =
        AvaloniaProperty.Register<Ft4TimeBarControl, bool>(nameof(IsOddSlot), true);

    public static readonly StyledProperty<bool> IsTransmittingProperty =
        AvaloniaProperty.Register<Ft4TimeBarControl, bool>(nameof(IsTransmitting), false);

    public double SlotProgress
    {
        get => GetValue(SlotProgressProperty);
        set => SetValue(SlotProgressProperty, value);
    }

    public double TxWindowProgress
    {
        get => GetValue(TxWindowProgressProperty);
        set => SetValue(TxWindowProgressProperty, value);
    }

    public bool IsOddSlot
    {
        get => GetValue(IsOddSlotProperty);
        set => SetValue(IsOddSlotProperty, value);
    }

    public bool IsTransmitting
    {
        get => GetValue(IsTransmittingProperty);
        set => SetValue(IsTransmittingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(Brushes.Black, bounds);

        var slotBrush = IsOddSlot ? Brushes.OliveDrab : Brushes.Teal;
        var fillWidth = bounds.Width * Math.Clamp(SlotProgress, 0, 1);
        context.FillRectangle(slotBrush, new Rect(0, 0, fillWidth, bounds.Height));

        if (IsTransmitting)
        {
            var txWidth = bounds.Width * Math.Clamp(TxWindowProgress, 0, 1);
            context.FillRectangle(Brushes.IndianRed, new Rect(0, 0, txWidth, bounds.Height));
        }
    }
}
