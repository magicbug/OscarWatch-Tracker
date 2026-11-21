using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cw;
using OscarWatch.Hardware;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

/// <summary>Type-ahead CW keyboard: characters are queued as they are typed.</summary>
public sealed partial class CwKeyboardViewModel : ObservableObject, IDisposable
{
    private readonly CwKeyerService _keyer;
    private readonly Func<CwMacroContext> _buildContext;
    private readonly ILocalizationService _l;
    private readonly object _gate = new();
    private readonly List<string> _pendingSegments = [];
    private readonly StringBuilder _token = new();
    private bool _inToken;
    private bool _disposed;

    public CwKeyboardViewModel(
        CwKeyerService keyer,
        Func<CwMacroContext> buildContext,
        ILocalizationService localization)
    {
        _keyer = keyer;
        _buildContext = buildContext;
        _l = localization;
        _keyer.KeyboardProgress += OnKeyboardProgress;
        _keyer.KeyboardSaveQsoRequested += OnKeyboardSaveQsoRequested;
    }

    /// <summary>Raised when a queued <c>{SAVEQSO}</c> marker is reached (UI thread).</summary>
    public event EventHandler? SaveQsoRequested;

    /// <summary>Raised when the operator asks to close the window.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when typing starts or resumes a keyboard keyer session.</summary>
    public event EventHandler? SessionActive;

    [ObservableProperty]
    private string _committedText = "";

    [ObservableProperty]
    private string _pendingText = "";

    [ObservableProperty]
    private string _draftText = "";

    public string Hint => _l.Get("Logbook.Cw.Keyboard.Hint");

    /// <summary>Start the keyer session if it is not already the keyboard.</summary>
    public CwKeyerStartResult EnsureSession()
    {
        if (_keyer.IsKeyboardSession)
            return CwKeyerStartResult.Started;

        var result = _keyer.BeginKeyboard();
        if (result == CwKeyerStartResult.Started)
            SessionActive?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Type or paste characters. Unsupported characters are ignored.</summary>
    public void TypeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (EnsureSession() != CwKeyerStartResult.Started)
            return;

        foreach (var c in text)
            TypeChar(c);
    }

    public void TypeChar(char c)
    {
        if (EnsureSession() != CwKeyerStartResult.Started)
            return;

        if (_inToken)
        {
            if (c == '}')
            {
                var token = _token.ToString();
                _token.Clear();
                _inToken = false;
                DraftText = "";
                ExpandAndEnqueue("{" + token + "}");
                return;
            }

            if (c == '{')
            {
                _token.Clear();
                DraftText = "{";
                return;
            }

            if (char.IsControl(c))
                return;

            _token.Append(c);
            DraftText = "{" + _token;
            return;
        }

        if (c == '{')
        {
            _inToken = true;
            _token.Clear();
            DraftText = "{";
            return;
        }

        if (char.IsWhiteSpace(c))
        {
            EnqueueSymbol(CwSymbol.WordGap, " ");
            return;
        }

        var upper = char.ToUpperInvariant(c);
        if (!MorseCode.IsSupported(upper))
            return;

        EnqueueSymbol(CwSymbol.Character(upper), upper.ToString());
    }

    /// <summary>Delete the last unsent symbol, or the unfinished <c>{TOKEN</c> draft.</summary>
    public bool TryBackspace()
    {
        if (_inToken)
        {
            if (_token.Length > 0)
            {
                _token.Length--;
                DraftText = "{" + _token;
                return true;
            }

            _inToken = false;
            DraftText = "";
            return true;
        }

        if (!_keyer.TryDropLastUnsent())
            return false;

        lock (_gate)
        {
            if (_pendingSegments.Count == 0)
                return true;

            _pendingSegments.RemoveAt(_pendingSegments.Count - 1);
            PendingText = string.Concat(_pendingSegments);
        }

        return true;
    }

    /// <summary>Append an expanded macro (and optional save marker) to the type-ahead queue.</summary>
    public bool AppendMacro(CwExpansion expansion)
    {
        if (EnsureSession() != CwKeyerStartResult.Started)
            return false;

        if (expansion.Message.IsEmpty && !expansion.SaveQso)
            return false;

        ClearDraft();

        foreach (var symbol in expansion.Message.Symbols)
            EnqueueSymbol(symbol, FormatSymbol(symbol));

        if (expansion.SaveQso)
            _keyer.Enqueue(CwMessage.Empty, saveQso: true);

        return true;
    }

    /// <summary>Abort sending and clear unsent text. The window stays open.</summary>
    public void Abort()
    {
        _keyer.Stop();
        lock (_gate)
        {
            _pendingSegments.Clear();
            PendingText = "";
        }

        ClearDraft();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _keyer.KeyboardProgress -= OnKeyboardProgress;
        _keyer.KeyboardSaveQsoRequested -= OnKeyboardSaveQsoRequested;
        if (_keyer.IsKeyboardSession)
            _keyer.Stop();
    }

    private void EnqueueSymbol(CwSymbol symbol, string display)
    {
        if (!_keyer.Enqueue(symbol))
            return;

        lock (_gate)
        {
            _pendingSegments.Add(display);
            PendingText = string.Concat(_pendingSegments);
        }
    }

    private void ExpandAndEnqueue(string template)
    {
        var expansion = CwMacroExpander.Expand(template, _buildContext());
        foreach (var symbol in expansion.Message.Symbols)
            EnqueueSymbol(symbol, FormatSymbol(symbol));

        if (expansion.SaveQso)
            _keyer.Enqueue(CwMessage.Empty, saveQso: true);
    }

    private void ClearDraft()
    {
        _inToken = false;
        _token.Clear();
        DraftText = "";
    }

    private void OnKeyboardProgress(object? sender, CwKeyboardProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
            {
                if (_pendingSegments.Count == 0)
                    return;

                var segment = _pendingSegments[0];
                _pendingSegments.RemoveAt(0);
                CommittedText += segment;
                PendingText = string.Concat(_pendingSegments);
            }
        });

    private void OnKeyboardSaveQsoRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => SaveQsoRequested?.Invoke(this, EventArgs.Empty));

    private static string FormatSymbol(CwSymbol symbol) =>
        symbol.IsProsign ? "<" + symbol.Value + ">" : symbol.Value;
}
