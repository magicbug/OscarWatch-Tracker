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
        Gs232LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                Gs232LogBox.CaretIndex = Gs232LogBox.Text?.Length ?? 0;
        };
        TraceTailBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                TraceTailBox.CaretIndex = TraceTailBox.Text?.Length ?? 0;
        };
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
        var extension = Path.GetExtension(suggestedFileName).TrimStart('.');
        if (extension.Length == 0)
            extension = "txt";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.Get("Save.Title"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }]
        });
        return file?.TryGetLocalPath();
    }
}
