using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Cw;

/// <summary>How a word in the copy is marked, from the logbook.</summary>
public enum CwCopyHighlight
{
    None,
    NewCall,
    WorkedCall,
    NewGrid,
    WorkedGrid,
    /// <summary>The operator's own callsign: someone is calling you.</summary>
    You,
}

/// <summary>One word of copy in the CW window.</summary>
public class CwCopyWord(string text, CwCopyHighlight highlight = CwCopyHighlight.None) : ObservableObject
{
    /// <summary>The same shades FT4 uses for new call, new grid and calling me.</summary>
    public static readonly IBrush NewCallBrush = Brush.Parse(Ft4DecodeHighlight.DefaultNewCallColour);
    public static readonly IBrush NewGridBrush = Brush.Parse(Ft4DecodeHighlight.DefaultNewGridColour);
    public static readonly IBrush YouBrush = Brush.Parse(Ft4DecodeHighlight.DefaultCallingMeColour);
    public static readonly IBrush WorkedBrush = Brush.Parse("#33909090");

    private CwCopyHighlight _highlight = highlight;

    public string Text { get; } = text;

    public CwCopyHighlight Highlight
    {
        get => _highlight;
        set
        {
            if (SetProperty(ref _highlight, value))
                OnPropertyChanged(nameof(Background));
        }
    }

    public IBrush Background => _highlight switch
    {
        CwCopyHighlight.NewCall => NewCallBrush,
        CwCopyHighlight.NewGrid => NewGridBrush,
        CwCopyHighlight.You => YouBrush,
        CwCopyHighlight.WorkedCall or CwCopyHighlight.WorkedGrid => WorkedBrush,
        _ => Brushes.Transparent,
    };
}

/// <summary>A word the logbook can take: a callsign or a Maidenhead locator.</summary>
public sealed class CwCopyLink(string text, CwTokenKind kind, string value, string tip) : CwCopyWord(text)
{
    private string _tip = tip;

    public CwTokenKind Kind { get; } = kind;

    /// <summary>What goes to the logbook (punctuation stripped, upper case).</summary>
    public string Value { get; } = value;

    public string Tip
    {
        get => _tip;
        set => SetProperty(ref _tip, value);
    }

    public bool IsCallsign => Kind == CwTokenKind.Callsign;
}

/// <summary>One line of the transcript, stamped with the UTC time its first word was settled.</summary>
public sealed class CwCopyLine(DateTime startedUtc)
{
    public DateTime StartedUtc { get; } = startedUtc;

    public string Stamp => StartedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public ObservableCollection<CwCopyWord> Words { get; } = [];

    public int Length => Words.Sum(w => w.Text.Length) + Math.Max(0, Words.Count - 1);

    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>
/// The settled CW copy, as lines of words. A pause starts a new line, long overs wrap at a
/// word, and a word the decoder settled in two pieces is joined back together.
/// </summary>
public sealed class CwCopyLog(Func<string, CwToken, CwCopyWord> createWord)
{
    public static readonly TimeSpan NewLineAfterPause = TimeSpan.FromSeconds(15);
    public const int MaxLineLength = 64;
    public const int MaxLines = 500;

    private DateTime _lastAppendUtc = DateTime.MinValue;

    public ObservableCollection<CwCopyLine> Lines { get; } = [];

    public string? OwnCallsign { get; set; }

    public string? OwnLocator { get; set; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// Add settled text from the decoder. A leading space means a word gap before it; without
    /// one, the first word continues the last word on screen.
    /// </summary>
    public void Append(string text, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var line = Lines.Count > 0 ? Lines[^1] : null;
        var paused = utc - _lastAppendUtc > NewLineAfterPause;
        _lastAppendUtc = utc;
        if (line is null || paused)
            line = StartLine(utc);

        var joinFirst = !paused && !char.IsWhiteSpace(text[0]) && line.Words.Count > 0;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (i == 0 && joinFirst)
            {
                var last = line.Words[^1];
                line.Words[^1] = Classify(last.Text + words[0]);
                continue;
            }

            if (line.Words.Count > 0 && line.Length + 1 + words[i].Length > MaxLineLength)
                line = StartLine(utc);
            line.Words.Add(Classify(words[i]));
        }
    }

    /// <summary>Every word on screen, oldest first, for re-marking after the logbook changes.</summary>
    public IEnumerable<CwCopyWord> AllWords => Lines.SelectMany(l => l.Words);

    public void Clear()
    {
        Lines.Clear();
        _lastAppendUtc = DateTime.MinValue;
    }

    /// <summary>The copy as plain text, one stamped line per transcript line.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        foreach (var line in Lines)
            output.Append(line.Stamp).Append("  ").AppendLine(line.Text);
        return output.ToString();
    }

    private CwCopyLine StartLine(DateTime utc)
    {
        var line = new CwCopyLine(utc);
        Lines.Add(line);
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
        return line;
    }

    private CwCopyWord Classify(string word)
    {
        var kind = CwCopySpotter.Classify(word, OwnCallsign, OwnLocator, out var value);
        return createWord(word, new CwToken(word, kind, value));
    }
}
