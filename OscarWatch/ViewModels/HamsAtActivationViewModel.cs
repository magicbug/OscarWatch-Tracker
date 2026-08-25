using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Display;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public sealed partial class HamsAtActivationViewModel : ObservableObject
{
    private readonly PassInfo _pass;
    private readonly GroundStation _observer;
    private readonly ILocalizationService _l;
    private readonly double? _defaultUplinkMhz;
    private readonly double? _defaultDownlinkMhz;

    public HamsAtActivationViewModel(
        PassInfo pass,
        GroundStation observer,
        string defaultCallsign,
        ILocalizationService localization,
        string timeRangeLine,
        string detailsLine,
        HamsAtActivationHints hints)
    {
        _pass = pass;
        _observer = observer;
        _l = localization;
        _defaultUplinkMhz = hints.UplinkMhz;
        _defaultDownlinkMhz = hints.DownlinkMhz;

        PassSummary = _l.Get(
            "HamsAt.Activation.PassSummary",
            pass.SatelliteName,
            timeRangeLine,
            detailsLine);

        Callsign = MaidenheadLocator.NormalizeCallsign(defaultCallsign);
        Grids = MaidenheadLocator.NormalizeGrids(observer.GridSquare);

        var available = hints.AvailableModes.Count > 0
            ? hints.AvailableModes
            : HamsAtApiModes.All;
        foreach (var mode in available)
            AvailableModes.Add(mode);

        SelectedMode = hints.SuggestedMode is { } suggested && AvailableModes.Contains(suggested)
            ? suggested
            : AvailableModes.FirstOrDefault();

        Comment = "";
        OpenOnHamsAtAfterPost = true;
        GridIsValid = MaidenheadLocator.GetLiveValidationState(Grids);
    }

    public string PassSummary { get; }

    public ObservableCollection<string> AvailableModes { get; } = [];

    public bool HasUplinkMhz => _defaultUplinkMhz is > 0;

    public bool HasDownlinkMhz => _defaultDownlinkMhz is > 0;

    public bool ShowMhzInput => IncludeUplinkMhz || IncludeDownlinkMhz;

    [ObservableProperty]
    private string _callsign = "";

    [ObservableProperty]
    private string _grids = "";

    [ObservableProperty]
    private string? _selectedMode;

    [ObservableProperty]
    private string _comment = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMhzInput))]
    private bool _includeUplinkMhz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMhzInput))]
    private bool _includeDownlinkMhz;

    [ObservableProperty]
    private string _selectedMhzText = "";

    [ObservableProperty]
    private bool _openOnHamsAtAfterPost = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string _errorText = "";

    [ObservableProperty]
    private bool? _gridIsValid;

    public bool HasErrorText => !string.IsNullOrWhiteSpace(ErrorText);

    partial void OnCallsignChanged(string value)
    {
        var normalized = MaidenheadLocator.NormalizeCallsign(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            Callsign = normalized;
    }

    partial void OnGridsChanged(string value)
    {
        CoerceGrid(value);
        GridIsValid = MaidenheadLocator.GetLiveValidationState(Grids);
    }

    partial void OnIncludeUplinkMhzChanged(bool value)
    {
        if (!value)
        {
            if (!IncludeDownlinkMhz)
                SelectedMhzText = "";
            return;
        }

        IncludeDownlinkMhz = false;
        SelectedMhzText = FormatMhz(_defaultUplinkMhz);
    }

    partial void OnIncludeDownlinkMhzChanged(bool value)
    {
        if (!value)
        {
            if (!IncludeUplinkMhz)
                SelectedMhzText = "";
            return;
        }

        IncludeUplinkMhz = false;
        SelectedMhzText = FormatMhz(_defaultDownlinkMhz);
    }

    public bool TryConfirm([NotNullWhen(true)] out HamsAtCreateAlertRequest? request)
    {
        request = null;
        ErrorText = "";

        var callsign = MaidenheadLocator.NormalizeCallsign(Callsign);
        if (callsign.Length < 3)
        {
            ErrorText = _l.Get("HamsAt.Activation.Error.CallsignRequired");
            return false;
        }

        if (string.IsNullOrWhiteSpace(Grids))
        {
            ErrorText = _l.Get("HamsAt.Activation.Error.GridsRequired");
            return false;
        }

        if (!MaidenheadLocator.TryValidateGrids(Grids, out var normalizedGrids, out var gridError, out var invalidSegment))
        {
            ErrorText = gridError switch
            {
                GridValidationError.TooManyGrids =>
                    _l.Get("Logbook.Error.GridTooMany", MaidenheadLocator.MaxGridCount),
                GridValidationError.InvalidSegment =>
                    _l.Get("Logbook.Error.GridInvalidSegment", invalidSegment ?? ""),
                _ => _l.Get("HamsAt.Activation.Error.GridsRequired")
            };
            return false;
        }

        var gridList = normalizedGrids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (gridList.Length == 0)
        {
            ErrorText = _l.Get("HamsAt.Activation.Error.GridsRequired");
            return false;
        }

        foreach (var grid in gridList)
        {
            if (grid.Length is not (4 or 6))
            {
                ErrorText = _l.Get("HamsAt.Activation.Error.GridLength", grid);
                return false;
            }
        }

        if (!int.TryParse(_pass.NoradId, out var norad) || norad <= 0)
        {
            ErrorText = _l.Get("Pass.HamsAt.InvalidNorad");
            return false;
        }

        var mode = SelectedMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode) || !HamsAtApiModes.All.Contains(mode))
        {
            ErrorText = _l.Get("HamsAt.Activation.Error.ModeRequired");
            return false;
        }

        double? mhz = null;
        string? mhzDirection = null;
        if (IncludeUplinkMhz || IncludeDownlinkMhz)
        {
            if (!TryParseMhz(SelectedMhzText, out var parsedMhz))
            {
                ErrorText = _l.Get("HamsAt.Activation.Error.MhzInvalid");
                return false;
            }

            mhz = parsedMhz;
            // hams.at: mhz_direction says whether mhz is uplink or downlink (default "down").
            mhzDirection = IncludeUplinkMhz ? "up" : "down";
        }

        var comment = Comment.Trim();
        if (comment.Length > 50)
            comment = comment[..50];

        request = new HamsAtCreateAlertRequest
        {
            SatelliteNumber = norad,
            ObserverLat = _observer.LatitudeDeg,
            ObserverLon = _observer.LongitudeDeg,
            MaxAtUtc = PassUtc.Normalize(_pass.MaxElevationUtc),
            Callsign = callsign,
            Grids = gridList,
            Mode = mode,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment,
            Mhz = mhz,
            MhzDirection = mhzDirection
        };
        return true;
    }

    private static string FormatMhz(double? mhz) =>
        mhz is > 0 ? mhz.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

    private static bool TryParseMhz(string? text, out double? mhz)
    {
        mhz = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        mhz = parsed;
        return true;
    }

    private void CoerceGrid(string value)
    {
        var normalized = MaidenheadLocator.UppercaseGridEntry(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            Grids = normalized;
    }
}
