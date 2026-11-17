using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Display;
using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>
/// The Moon (EME) window. Once a second it reads the map time, builds an <see cref="EmeFrame"/> for the
/// station, and updates the readable text. The timer exists only while the window is open, so nothing runs
/// when it is closed or minimised. The window is read-only: it never touches a radio or a rotator.
/// </summary>
public sealed partial class EmeViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EmeViewModel>();

    /// <summary>How long a worked-out rise or set is reused, so the search is not repeated every second.</summary>
    private static readonly TimeSpan NextEventLifetime = TimeSpan.FromMinutes(5);

    private readonly ISettingsService _settings;
    private readonly ILiveTrackingService _tracking;
    private readonly ILocalizationService _l;
    private readonly Func<DateTime> _utcNow;

    private MoonNextEvent? _nextEvent;
    private DateTime _nextEventComputedFor = DateTime.MinValue;
    private string _nextEventKey = "";
    private EmeFrame? _summaryFrame;
    private DispatcherTimer? _timer;
    private bool _disposed;
    private bool _loading;

    public EmeViewModel(
        ISettingsService settings,
        ILiveTrackingService tracking,
        ILocalizationService localization,
        Func<DateTime>? utcNow = null)
    {
        _settings = settings;
        _tracking = tracking;
        _l = localization;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _accessibleName = EmeTextBuilder.AccessibleName(_l);
        _emptyMessage = "";
        LoadFromSettings();
    }

    /// <summary>The bands offered in the band list.</summary>
    public IReadOnlyList<EmeBand> Bands => EmeBands.All;

    [ObservableProperty] private EmeBand _selectedBand = EmeBands.Default;

    /// <summary>The frequency as the operator typed it, in megahertz.</summary>
    [ObservableProperty] private string _frequencyText = "";

    /// <summary>Says why the frequency is not used. Empty while it is valid.</summary>
    [ObservableProperty] private string _frequencyError = "";

    [ObservableProperty] private string _beamwidthText = "";
    [ObservableProperty] private bool _isCircular;
    [ObservableProperty] private bool _largeDigits;

    /// <summary>What to show. Null when the Moon's position cannot be worked out.</summary>
    [ObservableProperty] private EmeFrame? _frame;
    [ObservableProperty] private bool _hasFrame;
    [ObservableProperty] private IReadOnlyList<EarthViewStatusSection> _statusSections = [];
    [ObservableProperty] private string _emptyMessage;
    [ObservableProperty] private string _accessibleName;
    [ObservableProperty] private string _accessibleSummary = "";

    /// <summary>"Up (at or above 0.0°)" or "Below the limit of 0.0°". Empty while there is nothing to show.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The short word for the large display: "Up" or "Down".</summary>
    [ObservableProperty] private string _shortStatus = "";

    /// <summary>Azimuth for the large display, to one decimal place.</summary>
    [ObservableProperty] private string _largeAzimuth = "";

    /// <summary>Elevation for the large display, to one decimal place.</summary>
    [ObservableProperty] private string _largeElevation = "";

    /// <summary>The window as plain text, for the copy button.</summary>
    [ObservableProperty] private string _plainText = "";

    public bool IsUp => Frame?.IsUp == true;

    /// <summary>Starts the clock. Call when the window opens.</summary>
    public void Start()
    {
        if (_disposed)
            return;
        Refresh();
        _timer ??= CreateTimer();
        _timer.Start();
    }

    /// <summary>Stops the clock. Call when the window is closed or minimised.</summary>
    public void Stop() => _timer?.Stop();

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        return timer;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var eme = _settings.Current.Eme;
            SelectedBand = EmeBands.Find(eme.SelectedBandId) ?? EmeBands.Default;
            LoadBandFields(eme);
            LargeDigits = eme.LargeDigits;
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadBandFields(EmeSettings eme)
    {
        FrequencyText = eme.FrequencyMHzFor(SelectedBand.Id).ToString("0.######", CultureInfo.CurrentCulture);
        FrequencyError = "";
        BeamwidthText = eme.BeamwidthFor(SelectedBand.Id) is { } b ? b.ToString("0.##", CultureInfo.CurrentCulture) : "";
        IsCircular = eme.IsCircular(SelectedBand.Id);
    }

    partial void OnSelectedBandChanged(EmeBand value)
    {
        if (_loading)
            return;
        var eme = _settings.Current.Eme;
        eme.SelectedBandId = value.Id;
        _loading = true;
        try { LoadBandFields(eme); }
        finally { _loading = false; }
        _settings.RequestSave();
        Refresh();
    }

    partial void OnFrequencyTextChanged(string value)
    {
        if (_loading)
            return;
        if (TryParse(value, out var mhz) && EmeLinkMath.IsValidFrequency(mhz))
        {
            FrequencyError = "";
            _settings.Current.Eme.FrequencyMHzByBand[SelectedBand.Id] = mhz;
            _settings.RequestSave();
        }
        else
        {
            // Not a usable frequency: the saved value stays as it was, but the window shows no Doppler or loss.
            FrequencyError = _l.Get(EmeTextBuilder.KeyFrequencyInvalid);
        }
        Refresh();
    }

    partial void OnBeamwidthTextChanged(string value)
    {
        if (_loading)
            return;
        var eme = _settings.Current.Eme;
        if (string.IsNullOrWhiteSpace(value))
            eme.BeamwidthDegByBand.Remove(SelectedBand.Id);
        else if (TryParse(value, out var degrees) && degrees > 0 && degrees <= 180)
            eme.BeamwidthDegByBand[SelectedBand.Id] = degrees;
        else
            return;
        _settings.RequestSave();
        Refresh();
    }

    partial void OnIsCircularChanged(bool value)
    {
        if (_loading)
            return;
        _settings.Current.Eme.CircularByBand[SelectedBand.Id] = value;
        _settings.RequestSave();
    }

    partial void OnLargeDigitsChanged(bool value)
    {
        if (_loading)
            return;
        _settings.Current.Eme.LargeDigits = value;
        _settings.RequestSave();
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Reads the latest state and updates every property. Public to the tests through InternalsVisibleTo.</summary>
    internal void Refresh()
    {
        if (_disposed)
            return;
        try
        {
            var settings = _settings.Current;
            var site = settings.GroundStation;
            var station = new StationPosition(site.LatitudeDeg, site.LongitudeDeg, site.AltitudeMetersAsl);

            // The time the map shows, so scrubbing the map time previews the Moon. Falls back to the clock
            // before the first snapshot has been published.
            var snapshotUtc = _tracking.SnapshotUtc;
            var displayUtc = snapshotUtc > DateTime.MinValue ? snapshotUtc : _utcNow() + _tracking.MapTimeOffset;

            var next = NextEventFor(settings, station, displayUtc);
            var frame = EmeFrameBuilder.Build(
                displayUtc, station, settings.Eme, site.HorizonMask, next, frequencyIsUsable: FrequencyError.Length == 0);
            if (frame is null)
            {
                ShowNothing();
                return;
            }
            Publish(frame);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Moon window refresh failed");
        }
    }

    private MoonNextEvent? NextEventFor(AppSettings settings, StationPosition station, DateTime displayUtc)
    {
        var eme = settings.Eme;
        var key = FormattableString.Invariant(
            $"{station.LatitudeDeg:R}|{station.LongitudeDeg:R}|{station.HeightMetres:R}|{eme.ElevationLimitDeg:R}|{eme.MaximumElevationDeg:R}|{eme.UseHorizonMask}|{settings.GroundStation.HorizonMask.Points.Count}");
        var stale = key != _nextEventKey
            || _nextEvent is { } e && e.TimeUtc <= displayUtc
            || (displayUtc - _nextEventComputedFor).Duration() > NextEventLifetime;
        if (stale)
        {
            var mask = eme.UseHorizonMask ? settings.GroundStation.HorizonMask : null;
            var rule = new MoonVisibilityRule(eme.ElevationLimitDeg, eme.MaximumElevationDeg, mask);
            _nextEvent = MoonNextEvent.Find(rule, station, displayUtc);
            _nextEventComputedFor = displayUtc;
            _nextEventKey = key;
        }
        return _nextEvent;
    }

    private void Publish(EmeFrame frame)
    {
        Frame = frame;
        HasFrame = true;
        EmptyMessage = "";
        StatusSections = EmeTextBuilder.Sections(_l, frame, FormatTime);
        StatusText = EmeTextBuilder.StatusText(_l, frame);
        ShortStatus = _l.Get(frame.IsUp ? EmeTextBuilder.KeyShortUp : EmeTextBuilder.KeyShortDown);
        LargeAzimuth = frame.Sample.AzimuthDeg.ToString("0.0", CultureInfo.CurrentCulture);
        LargeElevation = frame.Sample.ElevationDeg.ToString("0.0", CultureInfo.CurrentCulture);
        PlainText = EmeTextBuilder.PlainText(_l, frame, FormatTime);
        OnPropertyChanged(nameof(IsUp));
        if (frame.IsMaterialChange(_summaryFrame))
        {
            _summaryFrame = frame;
            AccessibleSummary = EmeTextBuilder.AccessibleSummary(_l, frame);
        }
    }

    private void ShowNothing()
    {
        Frame = null;
        HasFrame = false;
        _summaryFrame = null;
        StatusSections = [];
        StatusText = "";
        ShortStatus = "";
        LargeAzimuth = "";
        LargeElevation = "";
        PlainText = "";
        EmptyMessage = _l.Get(EmeTextBuilder.KeyEmptyNoPosition);
        AccessibleSummary = EmptyMessage;
        OnPropertyChanged(nameof(IsUp));
    }

    /// <summary>A date and time in the application's choice of UTC or local time and 12 or 24 hour clock.</summary>
    internal string FormatTime(DateTime utc)
    {
        var settings = _settings.Current;
        var clock = PassDisplayFormat.FromSettings(settings.Use24HourClock);
        var shown = settings.DisplayTimesInUtc ? utc : utc.ToLocalTime();
        var text = shown.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture)
            + shown.ToString(PassDisplayFormat.GetTimePattern(clock, includeSeconds: false), CultureInfo.CurrentCulture);
        return settings.DisplayTimesInUtc ? _l.Get(EmeTextBuilder.KeyValueTimeUtc, text) : text;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Stop();
        _timer = null;
    }
}
