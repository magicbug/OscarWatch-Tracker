using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class QsoLogbookWindow : Window
{
    public QsoLogbookWindow()
    {
        InitializeComponent();
        var settings = App.Services.GetRequiredService<ISettingsService>();
        QsoLogbookWindowBounds.Apply(this, settings.Current.QsoLogbook);
        Opened += OnOpened;
        Closed += OnClosed;
        // Tunnel so a focused text box or combo box (F4 opens its list) cannot take the macro keys first.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || e.KeyModifiers != KeyModifiers.None)
            return;

        if (e.Key is >= Key.F1 and <= Key.F12)
        {
            if (vm.TryHandleCwFunctionKey(e.Key - Key.F1))
                e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && vm.TryHandleCwEscape())
            e.Handled = true;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (QsoLogbookWindowDefaults.TryGetSavedPosition(settings.Current.QsoLogbook, out _, out _))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = QsoLogbookWindowBounds.ClampToVisibleArea(this, Position);
        }

        QsoLogbookHistoryGridLayout.Apply(QsoHistoryGrid, settings.Current.QsoLogbook);

        if (DataContext is QsoLogbookViewModel vm)
            await vm.InitializeAsync().ConfigureAwait(true);

        await Dispatcher.UIThread.InvokeAsync(() => CallTextBox?.Focus());
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        QsoLogbookHistoryGridLayout.Capture(QsoHistoryGrid, settings.Current.QsoLogbook);
        QsoLogbookWindowBounds.Capture(this, settings.Current.QsoLogbook);
        settings.RequestSave();

        if (DataContext is QsoLogbookViewModel vm)
            vm.Dispose();
    }

    private async void OnCommitQsoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm)
            return;

        await vm.CommitQsoCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (!vm.IsEditingQso)
            CallTextBox?.Focus();
    }

    private void OnCancelEditClick(object? sender, RoutedEventArgs e) => CallTextBox?.Focus();

    private void OnQsoWhenLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TemplatedControl picker)
            return;

        CenterPickerText(picker);
        picker.TemplateApplied += (_, _) => CenterPickerText(picker);
    }

    /// <summary>
    /// The Fluent date and time pickers pad the digits 3px from the top and 6px from the bottom,
    /// so the numbers sit above the mode box. Centre them on the same line.
    /// </summary>
    private static void CenterPickerText(TemplatedControl picker)
    {
        foreach (var text in picker.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.Name is not (
                "PART_DayTextBlock" or "PART_MonthTextBlock" or "PART_YearTextBlock"
                or "PART_HourTextBlock" or "PART_MinuteTextBlock" or "PART_SecondTextBlock"
                or "PART_PeriodTextBlock"))
            {
                continue;
            }

            text.Padding = new Thickness(0);
            text.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        }
    }

    private void OnQsoHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QsoHistoryGrid).Properties.IsRightButtonPressed)
            return;

        for (var node = e.Source as Visual; node is not null; node = node.GetVisualParent() as Visual)
        {
            if (node is DataGridRow row && row.DataContext is QsoRowViewModel qsoRow)
            {
                QsoHistoryGrid.SelectedItem = qsoRow;
                break;
            }
        }
    }

    private async Task<LogbookDetailsDialogResult?> ShowLogbookDetailsDialogAsync(
        CreateLogbookViewModel createVm)
    {
        return await new CreateLogbookWindow { DataContext = createVm }
            .ShowDialog<LogbookDetailsDialogResult?>(this)
            .ConfigureAwait(true);
    }

    private async void OnNewLogbookClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm)
            return;

        var createVm = App.Services.GetRequiredService<CreateLogbookViewModel>();
        createVm.ResetForCreate(vm.SelectedLogbook);
        var result = await ShowLogbookDetailsDialogAsync(createVm).ConfigureAwait(true);
        if (result is null || result.UpdateLogbookId.HasValue)
            return;

        await vm.CreateLogbookAsync(result).ConfigureAwait(true);
    }

    private async void OnEditLogbookClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var createVm = App.Services.GetRequiredService<CreateLogbookViewModel>();
        createVm.LoadForEdit(vm.SelectedLogbook);
        var result = await ShowLogbookDetailsDialogAsync(createVm).ConfigureAwait(true);
        if (result is null || !result.UpdateLogbookId.HasValue)
            return;

        await vm.UpdateLogbookAsync(result).ConfigureAwait(true);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var settingsVm = App.Services.GetRequiredService<LogbookSettingsViewModel>();
        settingsVm.LoadForLogbook(vm.SelectedLogbook);
        var result = await new LogbookSettingsWindow { DataContext = settingsVm }
            .ShowDialog<QsoLogbookCloudlogSettingsRequest?>(this)
            .ConfigureAwait(true);

        vm.ReloadCwKeyer();
        if (result is null)
            return;

        await vm.ApplyCloudlogSettingsAsync(result).ConfigureAwait(true);
    }

    private async void OnDeleteLogbookClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var l = LocalizationService.Instance;
        var name = vm.SelectedLogbook.Name;
        var confirmed = await SimpleConfirmDialog.ShowAsync(
            this,
            l.Get("Logbook.DeleteLogbook.ConfirmTitle"),
            l.Get("Logbook.DeleteLogbook.ConfirmMessage", name)).ConfigureAwait(true);

        if (!confirmed)
            return;

        await vm.DeleteSelectedLogbookAsync().ConfigureAwait(true);
    }

    private async void OnDeleteQsoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedQso is null)
            return;

        var l = LocalizationService.Instance;
        var qso = vm.SelectedQso;
        var confirmed = await SimpleConfirmDialog.ShowAsync(
            this,
            l.Get("Logbook.DeleteQso.ConfirmTitle"),
            l.Get("Logbook.DeleteQso.ConfirmMessage", qso.Call, qso.DateTimeText)).ConfigureAwait(true);

        if (!confirmed)
            return;

        await vm.DeleteSelectedQsoCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm)
            return;

        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            vm.ClearQsoFieldsCommand.Execute(null);
            e.Handled = true;
            CallTextBox?.Focus();
            return;
        }

        if (e.Key != Key.Enter)
            return;

        if (!vm.CommitQsoCommand.CanExecute(null))
            return;

        e.Handled = true;
        _ = CommitFromEntryAsync(vm);
    }

    private async Task CommitFromEntryAsync(QsoLogbookViewModel vm)
    {
        await vm.CommitQsoCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (!vm.IsEditingQso)
            CallTextBox?.Focus();
    }

    private async void OnExportAdifClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var dialogResult = await ExportAdifDialog.ShowAsync(
            this,
            vm.GetExportDialogDefaults(),
            vm.CountQsosForExportAsync).ConfigureAwait(true);
        if (dialogResult is null)
            return;

        var options = dialogResult.Options;
        var exportCount = await vm.CountQsosForExportAsync(options).ConfigureAwait(true);
        var adif = await vm.ExportAdifAsync(options).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(adif))
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance.Get("Logbook.ExportPicker.Title"),
            SuggestedFileName = QsoLogbookViewModel.BuildSuggestedExportFileName(vm.SelectedLogbook.Name, options),
            DefaultExtension = "adi",
            FileTypeChoices =
            [
                new FilePickerFileType("ADIF files") { Patterns = ["*.adi", "*.adif"] }
            ]
        });

        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(adif).ConfigureAwait(true);
        vm.StatusText = vm.FormatExportStatusText(options, exportCount);
    }
}
