using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Logbook;
using OscarWatch.Localization;

namespace OscarWatch.Views;

public sealed class ImportAdifPreviewDialogResult
{
    public required AdifImportPreview Preview { get; init; }
    public bool SatelliteOnly { get; init; }
}

public sealed partial class ImportAdifConflictRow : ObservableObject
{
    public required AdifImportCandidate Candidate { get; init; }

    [ObservableProperty]
    private bool _importAnyway;

    public string Call => Candidate.Qso.Call;
    public string Utc => Candidate.Qso.QsoUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public string Satellite => Candidate.Qso.SatName;
    public string Mode => Candidate.Qso.Mode;
    public string Grid => Candidate.Qso.GridSquare;
    public string Source { get; init; } = "";
}

/// <summary>Preview counts and satellite-only filter before writing an ADIF import.</summary>
public static class ImportAdifPreviewDialog
{
    public static async Task<ImportAdifPreviewDialogResult?> ShowAsync(
        Window owner,
        AdifImportParseResult parsed,
        IReadOnlySet<string> existingKeys,
        Func<AdifImportParseResult, IReadOnlySet<string>, bool, AdifImportPreview> buildPreview)
    {
        var l = LocalizationService.Instance;
        ImportAdifPreviewDialogResult? result = null;

        var satelliteOnlyCheck = new CheckBox
        {
            Content = l.Get("Logbook.ImportAdif.Dialog.SatelliteOnly"),
            IsChecked = false
        };

        var summaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };

        var importButton = new Button
        {
            Content = l.Get("Logbook.ImportAdif.Dialog.Import"),
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

        AdifImportPreview currentPreview = buildPreview(parsed, existingKeys, false);

        void RefreshSummary()
        {
            var satelliteOnly = satelliteOnlyCheck.IsChecked == true;
            currentPreview = buildPreview(parsed, existingKeys, satelliteOnly);

            var lines = new List<string>();
            lines.Add(currentPreview.NewCount switch
            {
                0 => l.Get("Logbook.ImportAdif.Dialog.Count.NewNone"),
                1 => l.Get("Logbook.ImportAdif.Dialog.Count.NewOne"),
                _ => l.Get("Logbook.ImportAdif.Dialog.Count.NewMany", currentPreview.NewCount)
            });

            if (currentPreview.DuplicateCount == 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.DuplicateOne"));
            else if (currentPreview.DuplicateCount > 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.DuplicateMany", currentPreview.DuplicateCount));

            if (currentPreview.UnparsedCount == 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.UnparsedOne"));
            else if (currentPreview.UnparsedCount > 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.UnparsedMany", currentPreview.UnparsedCount));

            if (currentPreview.FilteredOutCount == 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.FilteredOne"));
            else if (currentPreview.FilteredOutCount > 1)
                lines.Add(l.Get("Logbook.ImportAdif.Dialog.Count.FilteredMany", currentPreview.FilteredOutCount));

            summaryText.Text = string.Join(Environment.NewLine, lines);
            importButton.IsEnabled = currentPreview.NewCount > 0 || currentPreview.DuplicateCount > 0;
        }

        var window = new Window
        {
            Title = l.Get("Logbook.ImportAdif.Dialog.Title"),
            Width = 480,
            MinWidth = 360,
            MinHeight = 200,
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
                        Text = l.Get("Logbook.ImportAdif.Dialog.Intro"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray
                    },
                    satelliteOnlyCheck,
                    summaryText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancelButton, importButton }
                    }
                }
            }
        };

        satelliteOnlyCheck.IsCheckedChanged += (_, _) => RefreshSummary();
        importButton.Click += (_, _) =>
        {
            result = new ImportAdifPreviewDialogResult
            {
                Preview = currentPreview,
                SatelliteOnly = satelliteOnlyCheck.IsChecked == true
            };
            window.Close();
        };
        cancelButton.Click += (_, _) => window.Close();

        RefreshSummary();
        await window.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}

/// <summary>One duplicate contact: import anyway or ignore.</summary>
public static class ImportAdifSingleDuplicateDialog
{
    public enum Choice
    {
        Cancel,
        Ignore,
        ImportAnyway
    }

    public static async Task<Choice> ShowAsync(Window owner, AdifImportedQso qso)
    {
        var l = LocalizationService.Instance;
        var choice = Choice.Cancel;

        var detail = string.Join(
            "  ·  ",
            new[]
            {
                qso.Call,
                qso.QsoUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC",
                string.IsNullOrWhiteSpace(qso.SatName) ? null : qso.SatName,
                string.IsNullOrWhiteSpace(qso.Mode) ? null : qso.Mode
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var importAnyway = new Button
        {
            Content = l.Get("Logbook.ImportAdif.Duplicate.ImportAnyway"),
            Classes = { "dialog-footer", "accent" },
            MinWidth = 110,
            IsDefault = true
        };
        var ignore = new Button
        {
            Content = l.Get("Logbook.ImportAdif.Duplicate.Ignore"),
            Classes = { "dialog-footer" },
            MinWidth = 88
        };
        var cancel = new Button
        {
            Content = l.Get("Common.Cancel"),
            Classes = { "dialog-footer" },
            MinWidth = 88,
            IsCancel = true
        };

        var window = new Window
        {
            Title = l.Get("Logbook.ImportAdif.Duplicate.Title"),
            Width = 460,
            MinWidth = 360,
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
                        Text = l.Get("Logbook.ImportAdif.Duplicate.Message"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = detail,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancel, ignore, importAnyway }
                    }
                }
            }
        };

        importAnyway.Click += (_, _) =>
        {
            choice = Choice.ImportAnyway;
            window.Close();
        };
        ignore.Click += (_, _) =>
        {
            choice = Choice.Ignore;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        await window.ShowDialog(owner).ConfigureAwait(true);
        return choice;
    }
}

/// <summary>Table of duplicate contacts with per-row Import anyway ticks.</summary>
public static class ImportAdifConflictsDialog
{
    public static async Task<IReadOnlyList<AdifImportedQso>?> ShowAsync(
        Window owner,
        IReadOnlyList<AdifImportCandidate> duplicates)
    {
        var l = LocalizationService.Instance;
        IReadOnlyList<AdifImportedQso>? result = null;

        var rows = duplicates.Select(d => new ImportAdifConflictRow
        {
            Candidate = d,
            ImportAnyway = false,
            Source = d.DuplicateSource == AdifImportDuplicateSource.Logbook
                ? l.Get("Logbook.ImportAdif.Conflicts.Source.Logbook")
                : l.Get("Logbook.ImportAdif.Conflicts.Source.File")
        }).ToList();

        var selectAll = new CheckBox
        {
            Content = l.Get("Logbook.ImportAdif.Conflicts.SelectAll"),
            IsChecked = false
        };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = false,
            Height = 280,
            ItemsSource = rows,
            Columns =
            {
                new DataGridCheckBoxColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Import"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.ImportAnyway)) { Mode = BindingMode.TwoWay },
                    Width = new DataGridLength(70)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Call"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Call)),
                    IsReadOnly = true,
                    Width = new DataGridLength(90)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Utc"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Utc)),
                    IsReadOnly = true,
                    Width = new DataGridLength(130)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Satellite"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Satellite)),
                    IsReadOnly = true,
                    Width = new DataGridLength(90)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Mode"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Mode)),
                    IsReadOnly = true,
                    Width = new DataGridLength(70)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Grid"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Grid)),
                    IsReadOnly = true,
                    Width = new DataGridLength(80)
                },
                new DataGridTextColumn
                {
                    Header = l.Get("Logbook.ImportAdif.Conflicts.Source"),
                    Binding = new Binding(nameof(ImportAdifConflictRow.Source)),
                    IsReadOnly = true,
                    Width = new DataGridLength(140)
                }
            }
        };

        var importButton = new Button
        {
            Content = l.Get("Logbook.ImportAdif.Dialog.Import"),
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

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancelButton, importButton }
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var header = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                new TextBlock
                {
                    Text = l.Get("Logbook.ImportAdif.Conflicts.Intro"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                selectAll
            }
        };
        DockPanel.SetDock(header, Dock.Top);

        var panel = new DockPanel
        {
            Margin = new Thickness(16),
            LastChildFill = true
        };
        panel.Children.Add(footer);
        panel.Children.Add(header);
        panel.Children.Add(grid);

        var window = new Window
        {
            Title = l.Get("Logbook.ImportAdif.Conflicts.Title"),
            Width = 780,
            MinWidth = 560,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        selectAll.IsCheckedChanged += (_, _) =>
        {
            var tick = selectAll.IsChecked == true;
            foreach (var row in rows)
                row.ImportAnyway = tick;
        };

        importButton.Click += (_, _) =>
        {
            result = rows.Where(r => r.ImportAnyway).Select(r => r.Candidate.Qso).ToList();
            window.Close();
        };
        cancelButton.Click += (_, _) => window.Close();

        await window.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}
