using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using OscarWatch.Views;

namespace OscarWatch.Controls;

/// <summary>
/// Shared F1 to F12 CW keyer strip for the logbook and the CW decoder.
/// DataContext is a <see cref="ViewModels.CwKeyerPanelViewModel"/>.
/// </summary>
public partial class CwKeyerStrip : UserControl
{
    public static readonly StyledProperty<bool> ShowStatusProperty =
        AvaloniaProperty.Register<CwKeyerStrip, bool>(nameof(ShowStatus));

    private bool _editMessagesOpen;

    public CwKeyerStrip()
    {
        InitializeComponent();
    }

    /// <summary>Shows the keyer status line under the buttons (used on the decoder).</summary>
    public bool ShowStatus
    {
        get => GetValue(ShowStatusProperty);
        set => SetValue(ShowStatusProperty, value);
    }

    private void OnKeyboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CwKeyerPanelViewModel panel)
            return;

        var owner = this.GetVisualRoot() as Window;
        if (owner is null)
            return;

        panel.ToggleKeyboardFromStrip(owner);
    }

    private void OnEditMessagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint((Visual?)sender ?? this).Properties.IsRightButtonPressed)
            return;

        e.Handled = true;
        _ = OpenEditMessagesAsync();
    }

    private async Task OpenEditMessagesAsync()
    {
        if (_editMessagesOpen || DataContext is not CwKeyerPanelViewModel panel)
            return;

        if (!panel.TryBeginEditMessages())
            return;

        var owner = this.GetVisualRoot() as Window;
        if (owner is null)
            return;

        var logbook = App.Services.GetRequiredService<OscarWatch.Services.QsoLogbookEntryBridge>().Attached;
        if (logbook?.SelectedLogbook is null)
        {
            panel.StatusText = LocalizationService.Instance.Get("Logbook.Cw.Status.OpenLogbookToEdit");
            return;
        }

        _editMessagesOpen = true;
        try
        {
            var settingsVm = App.Services.GetRequiredService<LogbookSettingsViewModel>();
            settingsVm.LoadForLogbook(logbook.SelectedLogbook);
            var dialog = new LogbookSettingsWindow { DataContext = settingsVm };
            dialog.SelectCwKeyerTab();
            var result = await dialog.ShowDialog<QsoLogbookCloudlogSettingsRequest?>(owner).ConfigureAwait(true);

            panel.Reload();
            logbook.ReloadCwKeyer();
            logbook.ReloadAwardFields();
            if (result is not null)
                await logbook.ApplyCloudlogSettingsAsync(result).ConfigureAwait(true);
        }
        finally
        {
            _editMessagesOpen = false;
        }
    }
}
