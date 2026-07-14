using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class SessionPlannerWindow : Window
{
    public SessionPlannerWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionPlannerViewModel vm || string.IsNullOrEmpty(vm.LastExportContent))
            return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Session Plan as CSV",
            SuggestedFileName = "session-plan.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(vm.LastExportContent);
    }

    private async void OnExportIcsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionPlannerViewModel vm || string.IsNullOrEmpty(vm.LastExportContent))
            return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Session Plan as ICS",
            SuggestedFileName = "session-plan.ics",
            DefaultExtension = "ics",
            FileTypeChoices = [new FilePickerFileType("ICS Calendar") { Patterns = ["*.ics"] }]
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(vm.LastExportContent);
    }

    private async void OnSavePlanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionPlannerViewModel vm || string.IsNullOrEmpty(vm.LastExportContent))
            return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Session Plan",
            SuggestedFileName = "session-plan.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(vm.LastExportContent);
    }

    private async void OnLoadPlanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionPlannerViewModel vm)
            return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Session Plan",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });

        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new System.IO.StreamReader(stream);
        var json = await reader.ReadToEndAsync();

        vm.LoadPlanCommand.Execute(json);
    }
}
