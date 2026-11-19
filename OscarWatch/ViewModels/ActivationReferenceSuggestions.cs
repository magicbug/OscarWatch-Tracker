using System.Collections.ObjectModel;
using OscarWatch.Core.Logbook;

namespace OscarWatch.ViewModels;

/// <summary>
/// Keeps one reference box's suggestion list in step with the text the operator has typed.
/// For POTA lists only the text after the last comma is searched.
/// </summary>
public sealed class ActivationReferenceSuggestions
{
    private const int MaxSuggestions = 30;

    private readonly ActivationDirectoryService _directories;
    private readonly ActivationProgramme _programme;

    public ActivationReferenceSuggestions(ActivationDirectoryService directories, ActivationProgramme programme)
    {
        _directories = directories;
        _programme = programme;
    }

    public ObservableCollection<ActivationDirectoryEntry> Items { get; } = [];

    /// <summary>True for POTA, where the box may hold a comma-separated list of parks.</summary>
    public bool AllowList => _programme == ActivationProgramme.Pota;

    public void Update(string? text)
    {
        Items.Clear();
        var query = SearchQuery(text, AllowList);
        if (query.Length == 0)
            return;

        foreach (var entry in _directories.Search(_programme, query, MaxSuggestions))
            Items.Add(entry);
    }

    public static string SearchQuery(string? text, bool allowList)
    {
        var value = text ?? "";
        if (allowList)
        {
            var lastComma = value.LastIndexOf(',');
            if (lastComma >= 0)
                value = value[(lastComma + 1)..];
        }

        return value.Trim();
    }
}
