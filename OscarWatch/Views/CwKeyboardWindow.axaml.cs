using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class CwKeyboardWindow : Window
{
    public CwKeyboardWindow()
    {
        InitializeComponent();
        // Tunnel so Ctrl+V paste and character keys are not swallowed by focus chrome.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        Opened += (_, _) => Focus();
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CwKeyboardViewModel vm)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V)
        {
            var text = await GetClipboardTextAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(text))
            {
                vm.TypeText(text);
                e.Handled = true;
            }

            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Shift)
            return;

        switch (e.Key)
        {
            case Key.Back:
                if (vm.TryBackspace())
                    e.Handled = true;
                break;
            case Key.Escape:
                vm.Abort();
                e.Handled = true;
                break;
            case Key.Space:
                vm.TypeChar(' ');
                e.Handled = true;
                break;
        }
    }

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not CwKeyboardViewModel vm || string.IsNullOrEmpty(e.Text))
            return;

        vm.TypeText(e.Text);
        e.Handled = true;
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        var clipboard = Clipboard;
        if (clipboard is null)
            return null;

        return await ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true);
    }
}
