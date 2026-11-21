using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OscarWatch.ViewModels;

namespace OscarWatch.Controls;

public partial class UpcomingRovesListControl : UserControl
{
    public static readonly StyledProperty<bool> ShowResizeGripProperty =
        AvaloniaProperty.Register<UpcomingRovesListControl, bool>(nameof(ShowResizeGrip));

    public static readonly StyledProperty<double?> FixedListHeightProperty =
        AvaloniaProperty.Register<UpcomingRovesListControl, double?>(nameof(FixedListHeight));

    public bool ShowResizeGrip
    {
        get => GetValue(ShowResizeGripProperty);
        set => SetValue(ShowResizeGripProperty, value);
    }

    public double? FixedListHeight
    {
        get => GetValue(FixedListHeightProperty);
        set => SetValue(FixedListHeightProperty, value);
    }

    /// <summary>Raised when the operator presses the docked-panel resize grip.</summary>
    public event EventHandler<PointerPressedEventArgs>? ResizeGripPressed;

    public UpcomingRovesListControl()
    {
        InitializeComponent();
        PropertyChanged += OnControlPropertyChanged;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyChrome();
    }

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ShowResizeGripProperty || e.Property == FixedListHeightProperty)
            ApplyChrome();
    }

    private void ApplyChrome()
    {
        if (HamsAtRovesResizeGrip is not null)
            HamsAtRovesResizeGrip.IsVisible = ShowResizeGrip;

        if (HamsAtRovesListBox is null)
            return;

        var listRow = RootGrid?.RowDefinitions.Count > 2 ? RootGrid.RowDefinitions[2] : null;

        if (FixedListHeight is double height && !double.IsNaN(height) && height > 0)
        {
            // Sidebar: fixed height so the Auto grid row above Live/Sky plot stays stable.
            HamsAtRovesListBox.Height = height;
            HamsAtRovesListBox.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            if (listRow is not null)
                listRow.Height = GridLength.Auto;
            ClearValue(HeightProperty);
            ClearValue(VerticalAlignmentProperty);
        }
        else
        {
            // Floating window: fill the host and scroll inside the list.
            HamsAtRovesListBox.ClearValue(HeightProperty);
            HamsAtRovesListBox.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            if (listRow is not null)
                listRow.Height = new GridLength(1, GridUnitType.Star);
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }
    }

    private void OnHamsAtRovesResizePointerPressed(object? sender, PointerPressedEventArgs e) =>
        ResizeGripPressed?.Invoke(this, e);

    private void OnHamsAtRovesListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        for (var node = e.Source as Visual; node is not null; node = node.GetVisualParent() as Visual)
        {
            if (node is ListBoxItem { DataContext: HamsAtRoveRowViewModel row })
            {
                vm.OpenHamsAtRove(row);
                break;
            }
        }
    }
}
