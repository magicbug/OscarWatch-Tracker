using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OscarWatch.Core.Logbook;

namespace OscarWatch.Controls;

/// <summary>
/// Text box for a POTA, SOTA, or WWFF reference with a searchable suggestion list. Typing always
/// keeps the text; choosing a suggestion fills in the reference. With <see cref="AllowList"/> a
/// comma-separated list keeps the earlier entries and replaces only the last one.
/// </summary>
public partial class ActivationReferenceBox : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ActivationReferenceBox, string>(
            nameof(Text),
            defaultValue: "",
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<ActivationReferenceBox, string?>(nameof(Watermark));

    public static readonly StyledProperty<bool> AllowListProperty =
        AvaloniaProperty.Register<ActivationReferenceBox, bool>(nameof(AllowList));

    public static readonly StyledProperty<IEnumerable<ActivationDirectoryEntry>?> SuggestionsProperty =
        AvaloniaProperty.Register<ActivationReferenceBox, IEnumerable<ActivationDirectoryEntry>?>(nameof(Suggestions));

    private bool _suppressPopup;
    private bool _dismissed;
    private bool _keyboardMove;

    public ActivationReferenceBox()
    {
        InitializeComponent();
        Input.TextChanged += OnInputTextChanged;
        Input.KeyDown += OnInputKeyDown;
        Input.LostFocus += OnInputLostFocus;
        Items.SelectionChanged += OnItemsSelectionChanged;
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>When true, a comma-separated list is kept and only the last entry is replaced by a choice.</summary>
    public bool AllowList
    {
        get => GetValue(AllowListProperty);
        set => SetValue(AllowListProperty, value);
    }

    public IEnumerable<ActivationDirectoryEntry>? Suggestions
    {
        get => GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            var value = change.GetNewValue<string?>() ?? "";
            if (!string.Equals(Input.Text ?? "", value, StringComparison.Ordinal))
            {
                _suppressPopup = true;
                try
                {
                    Input.Text = value;
                    Input.CaretIndex = value.Length;
                }
                finally
                {
                    _suppressPopup = false;
                }

                _dismissed = true;
                Dropdown.IsOpen = false;
            }
        }
        else if (change.Property == WatermarkProperty)
        {
            Input.Watermark = change.GetNewValue<string?>();
        }
        else if (change.Property == SuggestionsProperty)
        {
            Items.ItemsSource = change.GetNewValue<IEnumerable<ActivationDirectoryEntry>?>();
        }
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        var typed = Input.Text ?? "";
        if (!string.Equals(typed, Text, StringComparison.Ordinal))
            SetCurrentValue(TextProperty, typed);

        if (_suppressPopup)
            return;

        _dismissed = false;
        UpdatePopup();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Dropdown.IsOpen:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up when Dropdown.IsOpen:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter when Dropdown.IsOpen && Items.SelectedItem is ActivationDirectoryEntry entry:
                Choose(entry);
                e.Handled = true;
                break;
            case Key.Escape when Dropdown.IsOpen:
                _dismissed = true;
                Dropdown.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Tab:
                Dropdown.IsOpen = false;
                break;
        }
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        // Keep the list open while the pointer is on it; light dismiss handles outside clicks.
        if (Items.IsPointerOver)
            return;
        Dropdown.IsOpen = false;
    }

    private void OnItemsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_keyboardMove || Items.SelectedItem is not ActivationDirectoryEntry entry)
            return;

        Choose(entry);
    }

    private void UpdatePopup()
    {
        var open = Input.IsFocused && !_dismissed && Items.ItemCount > 0 && !string.IsNullOrWhiteSpace(Input.Text);
        if (!open)
        {
            Dropdown.IsOpen = false;
            Items.SelectedIndex = -1;
            return;
        }

        Items.SelectedIndex = -1;
        Dropdown.IsOpen = true;
    }

    private void MoveSelection(int delta)
    {
        var count = Items.ItemCount;
        if (count == 0)
            return;

        var next = Items.SelectedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(Items.SelectedIndex + delta, 0, count - 1);

        _keyboardMove = true;
        try
        {
            Items.SelectedIndex = next;
        }
        finally
        {
            _keyboardMove = false;
        }
    }

    private void Choose(ActivationDirectoryEntry entry)
    {
        var current = Text ?? "";
        var value = entry.Reference;
        if (AllowList)
        {
            var lastComma = current.LastIndexOf(',');
            if (lastComma >= 0)
                value = current[..(lastComma + 1)] + entry.Reference;
        }

        _suppressPopup = true;
        try
        {
            Input.Text = value;
            Input.CaretIndex = value.Length;
        }
        finally
        {
            _suppressPopup = false;
        }

        SetCurrentValue(TextProperty, value);
        _dismissed = true;
        Dropdown.IsOpen = false;
        Items.SelectedIndex = -1;
        Input.Focus();
    }
}
