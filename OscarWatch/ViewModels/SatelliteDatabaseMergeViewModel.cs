using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class SatelliteDatabaseMergeViewModel : ViewModelBase
{
    private readonly ILocalizationService _l;
    private readonly SatelliteDatabaseMergePresentation _presentation;

    public SatelliteDatabaseMergeViewModel(
        SatelliteDatabaseMergePlan plan,
        ILocalizationService localization,
        SatelliteDatabaseMergePresentation presentation = SatelliteDatabaseMergePresentation.RemoteUpdate)
    {
        Plan = plan;
        _l = localization;
        _presentation = presentation;
        WindowTitle = presentation == SatelliteDatabaseMergePresentation.FileImport
            ? _l.Get("DbMerge.Title.Import")
            : _l.Get("DbMerge.Title.Remote");
        SummaryText = BuildSummary(plan, presentation);
        IntroHintText = BuildIntroHint(plan, presentation);
        ConflictRemoteLabel = presentation == SatelliteDatabaseMergePresentation.FileImport
            ? _l.Get("DbMerge.UseImported")
            : _l.Get("DbMerge.UsePublished");
        ConflictRemoteSummaryPrefix = presentation == SatelliteDatabaseMergePresentation.FileImport
            ? _l.Get("DbMerge.Prefix.Imported")
            : _l.Get("DbMerge.Prefix.Published");
        ConflictLocalLabel = _l.Get("DbMerge.UseLocal");

        foreach (var item in plan.NoradIdBackfills.OrderBy(i => i.SatelliteName, StringComparer.OrdinalIgnoreCase))
        {
            NoradIdBackfills.Add(new MergeNoradIdBackfillItem(
                item.Key,
                _l.Get("DbMerge.Item.NoradIdLine", item.SatelliteName, item.NoradId)));
        }

        foreach (var item in plan.PreferredNameUpdates.OrderBy(i => i.CurrentName, StringComparer.OrdinalIgnoreCase))
        {
            PreferredNameUpdates.Add(new MergePreferredNameUpdateItem(
                item.Key,
                _l.Get("DbMerge.Item.PreferredNameLine", item.CurrentName, item.NewPreferredName)));
        }

        foreach (var item in plan.NoticeUpdates.OrderBy(i => i.SatelliteName, StringComparer.OrdinalIgnoreCase))
        {
            NoticeUpdates.Add(new MergeNoticeUpdateItem(
                item.Key,
                BuildNoticeUpdateLabel(item)));
        }

        foreach (var item in plan.NewSatellites)
        {
            var label = item.Entry.Modes.Count == 1
                ? _l.Get("DbMerge.Item.SatelliteOneMode", item.Entry.Name)
                : _l.Get("DbMerge.Item.SatelliteManyModes", item.Entry.Name, item.Entry.Modes.Count);
            NewSatellites.Add(new MergeAdditionItem(item.Key, label));
        }

        foreach (var item in plan.NewModes)
        {
            NewModes.Add(new MergeAdditionItem(
                item.Key,
                _l.Get("DbMerge.Item.ModeLine", item.SatelliteName, item.Mode.Type)));
        }

        foreach (var item in plan.Conflicts)
        {
            Conflicts.Add(new MergeConflictItem(
                item.Key,
                item.SatelliteName,
                item.ModeType,
                SatelliteDatabaseMerger.DescribeMode(item.LocalMode),
                SatelliteDatabaseMerger.DescribeMode(item.RemoteMode),
                ConflictRemoteSummaryPrefix,
                ConflictRemoteLabel,
                ConflictLocalLabel,
                _l.Get("DbMerge.YoursFormat", SatelliteDatabaseMerger.DescribeMode(item.LocalMode))));
        }
    }

    public SatelliteDatabaseMergePlan Plan { get; }

    public string WindowTitle { get; }

    public string SummaryText { get; }

    public string IntroHintText { get; }

    public string ConflictRemoteLabel { get; }

    public string ConflictRemoteSummaryPrefix { get; }

    public string ConflictLocalLabel { get; }

    public bool HasNewSatellites => NewSatellites.Count > 0;
    public bool HasNewModes => NewModes.Count > 0;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasNoradIdBackfills => NoradIdBackfills.Count > 0;
    public bool HasPreferredNameUpdates => PreferredNameUpdates.Count > 0;
    public bool HasNoticeUpdates => NoticeUpdates.Count > 0;

    public ObservableCollection<MergeNoradIdBackfillItem> NoradIdBackfills { get; } = [];
    public ObservableCollection<MergePreferredNameUpdateItem> PreferredNameUpdates { get; } = [];
    public ObservableCollection<MergeNoticeUpdateItem> NoticeUpdates { get; } = [];
    public ObservableCollection<MergeAdditionItem> NewSatellites { get; } = [];
    public ObservableCollection<MergeAdditionItem> NewModes { get; } = [];
    public ObservableCollection<MergeConflictItem> Conflicts { get; } = [];

    public SatelliteDatabaseMergeSelection BuildSelection()
    {
        var selection = new SatelliteDatabaseMergeSelection();
        foreach (var item in NewSatellites.Where(i => i.IsSelected))
            selection.AcceptedNewSatelliteKeys.Add(item.Key);

        foreach (var item in NewModes.Where(i => i.IsSelected))
            selection.AcceptedNewModeKeys.Add(item.Key);

        foreach (var item in Conflicts.Where(i => i.UseRemote))
            selection.AcceptRemoteConflictKeys.Add(item.Key);

        foreach (var item in Conflicts.Where(i => i.UseLocal))
            selection.AcceptLocalConflictKeys.Add(item.Key);

        return selection;
    }

    public bool HasSelectedChanges()
    {
        if (Plan.NoradIdBackfills.Count > 0
            || Plan.PreferredNameUpdates.Count > 0
            || Plan.NoticeUpdates.Count > 0)
            return true;

        if (NewSatellites.Any(i => i.IsSelected) || NewModes.Any(i => i.IsSelected))
            return true;

        return Conflicts.Any(i => i.UseRemote || i.UseLocal);
    }

    private string BuildSummary(SatelliteDatabaseMergePlan plan, SatelliteDatabaseMergePresentation presentation)
    {
        var parts = new List<string>();
        if (plan.PreferredNameUpdates.Count > 0)
        {
            parts.Add(plan.PreferredNameUpdates.Count == 1
                ? _l.Get("DbMerge.Summary.OneRename", plan.PreferredNameUpdates.Count)
                : _l.Get("DbMerge.Summary.ManyRenames", plan.PreferredNameUpdates.Count));
        }

        if (plan.NoradIdBackfills.Count > 0)
        {
            parts.Add(plan.NoradIdBackfills.Count == 1
                ? _l.Get("DbMerge.Summary.OneNoradId", plan.NoradIdBackfills.Count)
                : _l.Get("DbMerge.Summary.ManyNoradIds", plan.NoradIdBackfills.Count));
        }

        if (plan.NoticeUpdates.Count > 0)
        {
            parts.Add(plan.NoticeUpdates.Count == 1
                ? _l.Get("DbMerge.Summary.OneNotice", plan.NoticeUpdates.Count)
                : _l.Get("DbMerge.Summary.ManyNotices", plan.NoticeUpdates.Count));
        }

        if (plan.NewSatellites.Count > 0)
        {
            parts.Add(plan.NewSatellites.Count == 1
                ? _l.Get("DbMerge.Summary.OneNewSatellite", plan.NewSatellites.Count)
                : _l.Get("DbMerge.Summary.ManyNewSatellites", plan.NewSatellites.Count));
        }

        if (plan.NewModes.Count > 0)
        {
            parts.Add(plan.NewModes.Count == 1
                ? _l.Get("DbMerge.Summary.OneNewMode", plan.NewModes.Count)
                : _l.Get("DbMerge.Summary.ManyNewModes", plan.NewModes.Count));
        }

        if (plan.Conflicts.Count > 0)
        {
            parts.Add(plan.Conflicts.Count == 1
                ? _l.Get("DbMerge.Summary.OneConflict", plan.Conflicts.Count)
                : _l.Get("DbMerge.Summary.ManyConflicts", plan.Conflicts.Count));
        }

        var source = presentation == SatelliteDatabaseMergePresentation.FileImport
            ? _l.Get("DbMerge.Source.Import")
            : _l.Get("DbMerge.Source.Remote");
        return parts.Count == 0
            ? _l.Get("DbMerge.Summary.Matches", source)
            : string.Join(", ", parts) + _l.Get("DbMerge.Summary.FromSuffix", source);
    }

    private string BuildIntroHint(SatelliteDatabaseMergePlan plan, SatelliteDatabaseMergePresentation presentation)
    {
        if (plan.PreferredNameUpdates.Count > 0
            && plan.NoradIdBackfills.Count == 0
            && plan.NoticeUpdates.Count == 0
            && plan.NewSatellites.Count == 0
            && plan.NewModes.Count == 0
            && plan.Conflicts.Count == 0)
        {
            return _l.Get("DbMerge.Intro.Renames");
        }

        if (plan.NoradIdBackfills.Count > 0
            && plan.PreferredNameUpdates.Count == 0
            && plan.NoticeUpdates.Count == 0
            && plan.NewSatellites.Count == 0
            && plan.NewModes.Count == 0
            && plan.Conflicts.Count == 0)
        {
            return _l.Get("DbMerge.Intro.NoradIds");
        }

        if (plan.NoticeUpdates.Count > 0
            && plan.PreferredNameUpdates.Count == 0
            && plan.NoradIdBackfills.Count == 0
            && plan.NewSatellites.Count == 0
            && plan.NewModes.Count == 0
            && plan.Conflicts.Count == 0)
        {
            return _l.Get("DbMerge.Intro.Notices");
        }

        return presentation == SatelliteDatabaseMergePresentation.FileImport
            ? _l.Get("DbMerge.Intro.Import")
            : _l.Get("DbMerge.Intro.Remote");
    }

    private string BuildNoticeUpdateLabel(SatelliteDatabaseNoticeUpdate item)
    {
        if (item.Notices.Count == 0)
            return _l.Get("DbMerge.Item.NoticeCleared", item.SatelliteName);

        if (item.Notices.Count == 1)
        {
            return _l.Get(
                "DbMerge.Item.NoticeLine",
                item.SatelliteName,
                SatelliteNoticeEvaluator.DescribeNotice(item.Notices[0]));
        }

        return _l.Get("DbMerge.Item.NoticeMany", item.SatelliteName, item.Notices.Count);
    }
}

public sealed class MergeNoticeUpdateItem(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
}

public sealed class MergeNoradIdBackfillItem(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
}

public sealed class MergePreferredNameUpdateItem(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
}

public partial class MergeAdditionItem : ObservableObject
{
    public MergeAdditionItem(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected = true;
}

public partial class MergeConflictItem : ObservableObject
{
    public MergeConflictItem(
        string key,
        string satelliteName,
        string modeType,
        string localSummary,
        string remoteSummary,
        string remoteSummaryPrefix,
        string useRemoteLabel,
        string useLocalLabel,
        string yoursSummaryLine)
    {
        Key = key;
        SatelliteName = satelliteName;
        ModeType = modeType;
        LocalSummary = localSummary;
        RemoteSummary = remoteSummary;
        YoursSummaryLine = yoursSummaryLine;
        RemoteSummaryLine = $"{remoteSummaryPrefix}: {remoteSummary}";
        UseRemoteLabel = useRemoteLabel;
        UseLocalLabel = useLocalLabel;
        UseLocal = true;
    }

    public string Key { get; }
    public string SatelliteName { get; }
    public string ModeType { get; }
    public string LocalSummary { get; }
    public string RemoteSummary { get; }
    public string YoursSummaryLine { get; }
    public string RemoteSummaryLine { get; }
    public string UseRemoteLabel { get; }
    public string UseLocalLabel { get; }
    public string Title => $"{SatelliteName} · {ModeType}";

    [ObservableProperty]
    private bool _useRemote;

    [ObservableProperty]
    private bool _useLocal;

    partial void OnUseRemoteChanged(bool value)
    {
        if (value && UseLocal)
            UseLocal = false;
    }

    partial void OnUseLocalChanged(bool value)
    {
        if (value && UseRemote)
            UseRemote = false;
    }
}
