using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Ft4;

/// <summary>
/// Paints a decode row from the message, the QSO partner, and the two colour settings.
/// Binding order: message, partner call, calling-me colour, replying colour, my callsign.
/// </summary>
/// <summary>Two-way hex string to <see cref="Color"/> for the settings colour picker.</summary>
public sealed class Ft4HexColorConverter : IValueConverter
{
    public static readonly Ft4HexColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var normalized = Ft4DecodeHighlight.NormalizeColour(value as string);
        if (normalized is not null && Color.TryParse(normalized, out var color))
            return color;
        return Colors.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
            return null;
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

public sealed class Ft4HexBrushConverter : IValueConverter
{
    public static readonly Ft4HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Ft4DecodeRowBackgroundConverter.BrushFromHex(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class Ft4DecodeRowBackgroundConverter : IMultiValueConverter
{
    public static readonly Ft4DecodeRowBackgroundConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 5 || values[0] is not Ft4DecodedMessage message)
            return Brushes.Transparent;

        var partner = values[1] as string;
        var calling = values[2] as string;
        var replying = values[3] as string;
        var myCall = values[4] as string;
        var kind = Ft4DecodeHighlight.Classify(message, myCall, partner);
        var hex = kind switch
        {
            Ft4DecodeHighlightKind.Replying => replying,
            Ft4DecodeHighlightKind.CallingMe => calling,
            _ => null
        };
        return BrushFromHex(hex);
    }

    public static IBrush BrushFromHex(string? text)
    {
        var normalized = Ft4DecodeHighlight.NormalizeColour(text);
        if (normalized is null || !Color.TryParse(normalized, out var color))
            return Brushes.Transparent;
        return new SolidColorBrush(color);
    }
}
