using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OscarWatch.Controls;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class Ft4ConsoleWindow : Window
{
    public Ft4ConsoleWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not Ft4ConsoleViewModel vm)
            return;

        vm.Initialize();
        vm.AttachWaterfall(new WaterfallBridge(Waterfall));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not Ft4ConsoleViewModel vm)
            return;
        if (sender is not ListBox listBox)
            return;
        if (listBox.SelectedItem is Ft4MessageListItem item)
            vm.ReplyToMessageCommand.Execute(item);
    }

    private sealed class WaterfallBridge(Ft4WaterfallControl control) : Ft4WaterfallTarget
    {
        public void PushRow(byte[] row) => control.PushRow(row);
    }
}
