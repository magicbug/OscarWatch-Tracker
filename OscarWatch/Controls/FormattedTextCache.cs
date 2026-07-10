using Avalonia.Media;
using OscarWatch.Core.Models;

namespace OscarWatch.Controls;

/// <summary>
/// Caches FormattedText layout objects keyed by (name, fontSize) to eliminate
/// per-frame glyph shaping and measurement on the 4 Hz render path.
/// Also caches the shared Typeface and foreground/background brushes for labels.
/// </summary>
internal sealed class FormattedTextCache
{
    private readonly Dictionary<(string Name, double FontSize), FormattedText> _cache = new();
    private SolidColorBrush? _labelBrush;
    private SolidColorBrush? _backgroundBrush;
    private Typeface? _typeface;
    private Color _cachedForegroundColor;
    private Color _cachedBackgroundColor;
    private bool _typefaceInitialized;

    /// <summary>
    /// Returns a cached FormattedText for the given name and font size, creating one on first request.
    /// Recreates brushes if the palette colours have changed.
    /// </summary>
    public FormattedText Get(string name, double fontSize, UiPalette palette)
    {
        return Get(name, fontSize, palette.MapLabelForeground);
    }

    /// <summary>
    /// Returns a cached FormattedText for the given name, font size, and explicit foreground colour.
    /// Creates the text on first request; returns the cached instance on subsequent calls with the same key.
    /// </summary>
    public FormattedText Get(string name, double fontSize, Color foreground)
    {
        var key = (name, fontSize);
        if (!_cache.TryGetValue(key, out var text))
        {
            text = new FormattedText(
                name,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetTypeface(),
                fontSize,
                new SolidColorBrush(foreground))
            {
                MaxTextWidth = 120
            };
            _cache[key] = text;
        }

        return text;
    }

    /// <summary>
    /// Returns a cached SolidColorBrush for label foreground text, recreating if colour changed.
    /// </summary>
    public SolidColorBrush GetLabelBrush(UiPalette palette)
    {
        if (_labelBrush is null || _cachedForegroundColor != palette.MapLabelForeground)
        {
            _cachedForegroundColor = palette.MapLabelForeground;
            _labelBrush = new SolidColorBrush(_cachedForegroundColor);
        }

        return _labelBrush;
    }

    /// <summary>
    /// Returns a cached SolidColorBrush for label background, recreating if colour changed.
    /// </summary>
    public SolidColorBrush GetBackgroundBrush(UiPalette palette)
    {
        if (_backgroundBrush is null || _cachedBackgroundColor != palette.MapLabelBackground)
        {
            _cachedBackgroundColor = palette.MapLabelBackground;
            _backgroundBrush = new SolidColorBrush(_cachedBackgroundColor);
        }

        return _backgroundBrush;
    }

    /// <summary>
    /// Returns the cached Typeface instance used for all satellite labels.
    /// </summary>
    public Typeface GetTypeface()
    {
        if (!_typefaceInitialized)
        {
            _typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
            _typefaceInitialized = true;
        }

        return _typeface!.Value;
    }

    /// <summary>
    /// Removes cached entries for satellites no longer visible. Prevents unbounded growth
    /// as satellites go out of view.
    /// </summary>
    public void Evict(IReadOnlyList<SatelliteTrackState> visibleStates)
    {
        if (_cache.Count == 0)
            return;

        var keysToRemove = new List<(string Name, double FontSize)>();

        foreach (var key in _cache.Keys)
        {
            var found = false;
            for (var i = 0; i < visibleStates.Count; i++)
            {
                if (string.Equals(visibleStates[i].Name, key.Name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
            _cache.Remove(key);
    }

    /// <summary>
    /// Clears all cached entries. Called when track states composition changes
    /// or the colour palette changes (theme switch).
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _labelBrush = null;
        _backgroundBrush = null;
        _typeface = null;
        _typefaceInitialized = false;
    }
}
