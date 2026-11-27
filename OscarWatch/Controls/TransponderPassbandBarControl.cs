using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OscarWatch.Core.Display;

namespace OscarWatch.Controls;

/// <summary>
/// Compact horizontal transponder passband visualiser for the dark frequency overlay.
/// Renders the active passband between out-of-band gutters, sibling mode spots (such as FT4 or CW TLM),
/// and the live satellite-frame tuning cursor.
/// </summary>
public sealed class TransponderPassbandBarControl : Control
{
    private static readonly Typeface MonoTypeface = new("Consolas,Courier New,monospace", FontStyle.Normal, FontWeight.SemiBold);

    private const double SpotLabelSize = 12;

    private static readonly IBrush TrackBackgroundBrush = new SolidColorBrush(Color.Parse("#FF121212"));
    private static readonly IPen TrackBorderPen = new Pen(new SolidColorBrush(Color.Parse("#FF3A3A3A")), 1);
    private static readonly IBrush GutterFillBrush = new SolidColorBrush(Color.Parse("#99221A1A"));
    private static readonly IBrush PassbandFillBrush = new SolidColorBrush(Color.Parse("#FF1B281F"));
    private static readonly IPen PassbandEdgePen = new Pen(new SolidColorBrush(Color.Parse("#FF6B9E78")), 2);
    private static readonly IPen CentreLinePen = new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 1, new DashStyle([2, 2], 0));

    private static readonly IPen BeaconTickPen = new Pen(new SolidColorBrush(Color.Parse("#BFFFD27A")), 1.25);
    private static readonly IBrush BeaconLabelBrush = new SolidColorBrush(Color.Parse("#FFFFD27A"));

    private static readonly IBrush DigiSpotFillBrush = new SolidColorBrush(Color.Parse("#4038BDF8"));
    private static readonly IPen DigiSpotBorderPen = new Pen(new SolidColorBrush(Color.Parse("#B338BDF8")), 1);
    private static readonly IBrush DigiLabelBrush = new SolidColorBrush(Color.Parse("#FF7DD3FC"));

    private static readonly IBrush GenericSpotBrush = new SolidColorBrush(Color.Parse("#FFB0B0B0"));
    private static readonly IPen GenericSpotPen = new Pen(new SolidColorBrush(Color.Parse("#99B0B0B0")), 1);

    private static readonly IBrush InBandBrush = new SolidColorBrush(Color.Parse("#FF3DDC84"));
    private static readonly IPen InBandPen = new Pen(InBandBrush, 2);
    private static readonly IBrush NearEdgeBrush = new SolidColorBrush(Color.Parse("#FFFFD27A"));
    private static readonly IPen NearEdgePen = new Pen(NearEdgeBrush, 2);
    private static readonly IBrush OutOfBandBrush = new SolidColorBrush(Color.Parse("#FFE07070"));
    private static readonly IPen OutOfBandPen = new Pen(OutOfBandBrush, 2);
    private static readonly IPen PipOutlinePen = new Pen(new SolidColorBrush(Color.Parse("#FF121212")), 1);

    public static readonly StyledProperty<PassbandIndicatorState?> PassbandStateProperty =
        AvaloniaProperty.Register<TransponderPassbandBarControl, PassbandIndicatorState?>(nameof(PassbandState));

    static TransponderPassbandBarControl()
    {
        AffectsRender<TransponderPassbandBarControl>(PassbandStateProperty);
    }

    public PassbandIndicatorState? PassbandState
    {
        get => GetValue(PassbandStateProperty);
        set => SetValue(PassbandStateProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 8 || height <= 8)
            return;

        var state = PassbandState;
        var outerRect = new Rect(0.5, 0.5, width - 1, height - 1);
        const double radius = 4;

        using (context.PushClip(new RoundedRect(outerRect, new CornerRadius(radius))))
        {
            context.DrawRectangle(TrackBackgroundBrush, null, outerRect);

            if (state is not null)
            {
                var innerHeight = height - 2;
                var passLeft = width * PassbandIndicatorCalculator.GutterFraction;
                var passRight = width * (1.0 - PassbandIndicatorCalculator.GutterFraction);
                var passWidth = Math.Max(0, passRight - passLeft);

                context.DrawRectangle(GutterFillBrush, null, new Rect(1, 1, Math.Max(0, passLeft - 1), innerHeight));
                context.DrawRectangle(GutterFillBrush, null, new Rect(passRight, 1, Math.Max(0, width - passRight - 1), innerHeight));

                context.DrawRectangle(PassbandFillBrush, null, new Rect(passLeft, 1, passWidth, innerHeight));
                context.DrawLine(PassbandEdgePen, new Point(passLeft, 1), new Point(passLeft, height - 1));
                context.DrawLine(PassbandEdgePen, new Point(passRight, 1), new Point(passRight, height - 1));

                var centreX = passLeft + (passWidth * 0.5);
                context.DrawLine(CentreLinePen, new Point(centreX, 2), new Point(centreX, height - 2));

                DrawSpots(context, state, width, height, innerHeight);
            }
        }

        context.DrawRectangle(null, TrackBorderPen, outerRect, radius, radius);

        if (state is null)
            return;

        var (cursorBrush, cursorPen) = state.Zone switch
        {
            PassbandZone.OutOfBand => (OutOfBandBrush, OutOfBandPen),
            PassbandZone.NearEdge => (NearEdgeBrush, NearEdgePen),
            _ => (InBandBrush, InBandPen)
        };

        var cursorX = Math.Clamp(width * state.DownlinkMarkerPosition, 3, width - 3);
        context.DrawLine(cursorPen, new Point(cursorX, 1), new Point(cursorX, height - 1));
        context.DrawEllipse(cursorBrush, PipOutlinePen, new Point(cursorX, 5), 3.5, 3.5);
    }

    private static void DrawSpots(
        DrawingContext context,
        PassbandIndicatorState state,
        double width,
        double height,
        double innerHeight)
    {
        double lastLabelRight = -100;
        foreach (var spot in state.Spots)
        {
            var spotX = width * spot.NormalizedPosition;
            IBrush labelBrush;

            if (spot.SpanNormalized is > 0)
            {
                var spanPx = Math.Max(4, width * spot.SpanNormalized.Value);
                var rect = new Rect(spotX, 1, Math.Min(spanPx, width - spotX - 1), innerHeight);
                context.DrawRectangle(DigiSpotFillBrush, null, rect);
                context.DrawLine(DigiSpotBorderPen, new Point(rect.Left, 1), new Point(rect.Left, height - 1));
                context.DrawLine(DigiSpotBorderPen, new Point(rect.Right, 1), new Point(rect.Right, height - 1));
                labelBrush = DigiLabelBrush;
            }
            else if (spot.IsBeacon)
            {
                context.DrawLine(BeaconTickPen, new Point(spotX, 1), new Point(spotX, height - 1));
                labelBrush = BeaconLabelBrush;
            }
            else
            {
                context.DrawLine(GenericSpotPen, new Point(spotX, 1), new Point(spotX, height - 1));
                labelBrush = GenericSpotBrush;
            }

            if (string.IsNullOrWhiteSpace(spot.Label))
                continue;

            var text = new FormattedText(
                spot.Label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                SpotLabelSize,
                labelBrush);

            var labelX = spotX + 3;
            if (labelX + text.Width > width - 3)
                labelX = Math.Max(3, spotX - text.Width - 3);

            if (labelX < lastLabelRight + 3)
                continue;

            var labelY = Math.Max(1, height - text.Height - 2);
            context.DrawText(text, new Point(labelX, labelY));
            lastLabelRight = labelX + text.Width;
        }
    }
}
