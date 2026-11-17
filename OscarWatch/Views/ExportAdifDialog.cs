using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OscarWatch.Core.Logbook;
using OscarWatch.Localization;

namespace OscarWatch.Views;

public sealed class ExportAdifDialogResult
{
    public required QsoAdifExportOptions Options { get; init; }
}

public static class ExportAdifDialog
{
    public static async Task<ExportAdifDialogResult?> ShowAsync(
        Window owner,
        QsoAdifExportDialogDefaults defaults,
        Func<QsoAdifExportOptions, Task<int>> countExportQsosAsync)
    {
        var l = LocalizationService.Instance;
        ExportAdifDialogResult? result = null;

        var hasSelection = defaults.SelectedCount > 0;
        var preferSelected = defaults.InitialScope == QsoAdifExportScope.Selected && hasSelection;

        var selectedScopeRadio = new RadioButton
        {
            Content = defaults.SelectedCount switch
            {
                0 => l.Get("Logbook.ExportAdif.Dialog.Scope.SelectedNone"),
                1 => l.Get("Logbook.ExportAdif.Dialog.Scope.SelectedOne"),
                _ => l.Get("Logbook.ExportAdif.Dialog.Scope.SelectedMany", defaults.SelectedCount)
            },
            IsEnabled = hasSelection,
            IsChecked = preferSelected,
            GroupName = "ExportScope"
        };

        var allScopeRadio = new RadioButton
        {
            Content = l.Get("Logbook.ExportAdif.Dialog.Scope.All"),
            IsChecked = !preferSelected,
            GroupName = "ExportScope"
        };

        var dateRangeRadio = new RadioButton
        {
            Content = l.Get("Logbook.ExportAdif.Dialog.Scope.DateRange"),
            GroupName = "ExportScope"
        };

        var fromDatePicker = new DatePicker
        {
            SelectedDate = defaults.FromUtcDate,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 280
        };

        var toDatePicker = new DatePicker
        {
            SelectedDate = defaults.ToUtcDate,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 280
        };

        var dateRangePanel = new StackPanel
        {
            Margin = new Thickness(24, 0, 0, 0),
            IsEnabled = false,
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = l.Get("Logbook.ExportAdif.Dialog.FromUtc"),
                            FontSize = 12,
                            Foreground = Brushes.Gray
                        },
                        fromDatePicker
                    }
                },
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = l.Get("Logbook.ExportAdif.Dialog.ToUtc"),
                            FontSize = 12,
                            Foreground = Brushes.Gray
                        },
                        toDatePicker
                    }
                }
            }
        };

        var countText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray
        };

        var forLotwCheckBox = new CheckBox
        {
            Content = l.Get("Logbook.ExportAdif.Dialog.ForLotw"),
            IsChecked = false
        };

        var exportButton = new Button
        {
            Content = l.Get("Logbook.ExportAdif.Dialog.Export"),
            Classes = { "dialog-footer", "accent" },
            MinWidth = 88,
            IsDefault = true
        };

        var cancelButton = new Button
        {
            Content = l.Get("Common.Cancel"),
            Classes = { "dialog-footer" },
            MinWidth = 88,
            IsCancel = true
        };

        var window = new Window
        {
            Title = l.Get("Logbook.ExportAdif.Dialog.Title"),
            Width = 520,
            MinWidth = 400,
            MinHeight = 220,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = l.Get("Logbook.ExportAdif.Dialog.Intro"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray
                    },
                    selectedScopeRadio,
                    allScopeRadio,
                    dateRangeRadio,
                    dateRangePanel,
                    countText,
                    forLotwCheckBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancelButton, exportButton }
                    }
                }
            }
        };

        QsoAdifExportOptions BuildOptions()
        {
            var useSelected = selectedScopeRadio.IsChecked == true;
            var useDateRange = dateRangeRadio.IsChecked == true;
            var fromDate = (fromDatePicker.SelectedDate ?? defaults.FromUtcDate).UtcDateTime.Date;
            var toDate = (toDatePicker.SelectedDate ?? defaults.ToUtcDate).UtcDateTime.Date;
            var (normalizedFrom, normalizedTo) = QsoLogbookExportRange.NormalizeUtcDates(fromDate, toDate);

            return new QsoAdifExportOptions
            {
                Scope = useSelected
                    ? QsoAdifExportScope.Selected
                    : useDateRange
                        ? QsoAdifExportScope.DateRange
                        : QsoAdifExportScope.All,
                FromUtcDate = normalizedFrom,
                ToUtcDate = normalizedTo,
                ForLotw = forLotwCheckBox.IsChecked == true
            };
        }

        async Task RefreshPreviewAsync()
        {
            var options = BuildOptions();
            var count = await countExportQsosAsync(options).ConfigureAwait(true);
            countText.Text = count switch
            {
                0 => l.Get("Logbook.ExportAdif.Dialog.CountNone"),
                1 => l.Get("Logbook.ExportAdif.Dialog.CountOne"),
                _ => l.Get("Logbook.ExportAdif.Dialog.CountMany", count)
            };
            exportButton.IsEnabled = count > 0;
        }

        void SetDateRangeEnabled(bool enabled)
        {
            dateRangePanel.IsEnabled = enabled;
            fromDatePicker.IsEnabled = enabled;
            toDatePicker.IsEnabled = enabled;
        }

        selectedScopeRadio.IsCheckedChanged += async (_, _) =>
        {
            if (selectedScopeRadio.IsChecked != true)
                return;

            SetDateRangeEnabled(false);
            await RefreshPreviewAsync().ConfigureAwait(true);
        };

        allScopeRadio.IsCheckedChanged += async (_, _) =>
        {
            if (allScopeRadio.IsChecked != true)
                return;

            SetDateRangeEnabled(false);
            await RefreshPreviewAsync().ConfigureAwait(true);
        };

        dateRangeRadio.IsCheckedChanged += async (_, _) =>
        {
            if (dateRangeRadio.IsChecked != true)
                return;

            SetDateRangeEnabled(true);
            await RefreshPreviewAsync().ConfigureAwait(true);
        };

        fromDatePicker.SelectedDateChanged += async (_, _) => await RefreshPreviewAsync().ConfigureAwait(true);
        toDatePicker.SelectedDateChanged += async (_, _) => await RefreshPreviewAsync().ConfigureAwait(true);

        exportButton.Click += (_, _) =>
        {
            var options = BuildOptions();
            result = new ExportAdifDialogResult { Options = options };
            window.Close();
        };

        cancelButton.Click += (_, _) => window.Close();

        await RefreshPreviewAsync().ConfigureAwait(true);
        await window.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}
