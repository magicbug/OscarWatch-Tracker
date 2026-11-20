using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Logbook;
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
        // Tunnel so right-click on an already-selected row keeps multi-row highlight intact.
        QsoHistoryGrid.AddHandler(
            InputElement.PointerPressedEvent,
            OnQsoHistoryPointerPressed,
            RoutingStrategies.Tunnel);
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
            if (node is not DataGridRow row || row.DataContext is not QsoRowViewModel qsoRow)
                continue;

            if (QsoHistoryGrid.SelectedItems.Contains(qsoRow))
            {
                // Keep the existing multi-row highlight when right-clicking inside it.
                e.Handled = true;
            }
            else
            {
                QsoHistoryGrid.SelectedItem = qsoRow;
            }

            break;
        }
    }

    private void OnQsoHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not QsoLogbookViewModel vm)
            return;

        vm.SetSelectedQsos(QsoHistoryGrid.SelectedItems.OfType<QsoRowViewModel>().ToList());
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

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnImportAdifClick(object? sender, RoutedEventArgs e) =>
        await RunImportAdifAsync().ConfigureAwait(true);

    private async void OnExportAdifClick(object? sender, RoutedEventArgs e) =>
        await RunExportAdifAsync(QsoAdifExportScope.All).ConfigureAwait(true);

    private async void OnExportSelectedAdifClick(object? sender, RoutedEventArgs e) =>
        await RunExportAdifAsync(QsoAdifExportScope.Selected).ConfigureAwait(true);

    private async Task RunImportAdifAsync()
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance.Get("Logbook.ImportAdif.Picker.Title"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ADIF files") { Patterns = ["*.adi", "*.adif"] }
            ]
        }).ConfigureAwait(true);

        if (files.Count == 0)
            return;

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        var adifText = await reader.ReadToEndAsync().ConfigureAwait(true);

        var parsed = AdifImporter.Parse(adifText);
        if (parsed.Records.Count == 0 && parsed.UnparsedCount == 0)
        {
            vm.StatusText = LocalizationService.Instance.Get("Logbook.ImportAdif.Status.EmptyFile");
            return;
        }

        var keys = await App.Services.GetRequiredService<IQsoLogbookRepository>()
            .ListQsoDuplicateKeysAsync(vm.SelectedLogbook.Id)
            .ConfigureAwait(true);

        var previewResult = await ImportAdifPreviewDialog.ShowAsync(
            this,
            parsed,
            keys,
            AdifImportClassifier.BuildPreview).ConfigureAwait(true);
        if (previewResult is null)
            return;

        var toImport = new List<AdifImportedQso>(previewResult.Preview.NewQsos.Select(c => c.Qso));
        var duplicates = previewResult.Preview.Duplicates;

        if (duplicates.Count == 1)
        {
            var choice = await ImportAdifSingleDuplicateDialog.ShowAsync(this, duplicates[0].Qso)
                .ConfigureAwait(true);
            if (choice == ImportAdifSingleDuplicateDialog.Choice.Cancel)
                return;
            if (choice == ImportAdifSingleDuplicateDialog.Choice.ImportAnyway)
                toImport.Add(duplicates[0].Qso);
        }
        else if (duplicates.Count > 1)
        {
            var chosen = await ImportAdifConflictsDialog.ShowAsync(this, duplicates).ConfigureAwait(true);
            if (chosen is null)
                return;
            toImport.AddRange(chosen);
        }

        var imported = await vm.ImportAdifQsosAsync(toImport).ConfigureAwait(true);
        vm.StatusText = vm.FormatImportStatusText(imported);
    }

    private async Task RunExportAdifAsync(QsoAdifExportScope initialScope)
    {
        if (DataContext is not QsoLogbookViewModel vm || vm.SelectedLogbook is null)
            return;

        var dialogResult = await ExportAdifDialog.ShowAsync(
            this,
            vm.GetExportDialogDefaults(initialScope),
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
