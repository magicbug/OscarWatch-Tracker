using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

public partial class MainWindow : Window, ITroubleshootHost
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel(this);
        DataContext = viewModel;
        viewModel.Refresh();
        Activated += (_, _) => viewModel.Refresh();
    }

    public Task<bool> ConfirmAsync(string message) => ConfirmDialog.ShowAsync(this, message);

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is null)
            throw new InvalidOperationException("Clipboard unavailable");
        await Clipboard.SetTextAsync(text);
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.Get("Save.Title"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
        });
        return file?.TryGetLocalPath();
    }
}
