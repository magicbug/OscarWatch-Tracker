using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>
/// Earth View: follows the focused satellite and publishes what the window needs. Once a second it
/// reads the latest tracking snapshot (at map time, so scrubbing the map time moves the globe), builds an
/// <see cref="EarthViewFrame"/>, and updates the readable text. The timer exists only while the window
/// is open, so nothing runs in the background when it is closed or minimised.
/// </summary>
public sealed partial class EarthViewViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EarthViewViewModel>();

    private readonly ISettingsService _settings;
    private readonly ILiveTrackingService _tracking;
    private readonly ILocalizationService _l;
    private readonly Func<DateTime> _utcNow;
    private readonly HashSet<string> _loggedUnusableState = new(StringComparer.Ordinal);

    // The frame the accessible summary was last built from. The summary changes only on material change.
    private EarthViewFrame? _summaryFrame;

    private DispatcherTimer? _timer;
    private bool _disposed;

    public EarthViewViewModel(
        ISettingsService settings,
        ILiveTrackingService tracking,
        ILocalizationService localization,
        Func<DateTime>? utcNow = null)
    {
        _settings = settings;
        _tracking = tracking;
        _l = localization;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        _accessibleName = EarthViewTextBuilder.AccessibleName(_l, null);
        _emptyMessage = _l.Get(EarthViewTextBuilder.KeyEmptyNoSatellite);
    }

    /// <summary>What to draw. Null when there is nothing to show.</summary>
    [ObservableProperty] private EarthViewFrame? _frame;

    /// <summary>True when there is a satellite and a position to show.</summary>
    [ObservableProperty] private bool _hasSatellite;

    /// <summary>The facts beside the globe, in sections. Empty while there is nothing to show.</summary>
    [ObservableProperty] private IReadOnlyList<EarthViewStatusSection> _statusSections = [];

    /// <summary>The same facts as plain text.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Explains why there is no globe. Empty while there is one.</summary>
    [ObservableProperty] private string _emptyMessage;

    /// <summary>The control's accessible name, for example "Earth View, ISS".</summary>
    [ObservableProperty] private string _accessibleName;

    /// <summary>The control's accessible help text. Updated only when something material changes.</summary>
    [ObservableProperty] private string _accessibleSummary = "";

    /// <summary>Starts following the focused satellite. Call when the window opens.</summary>
    public void Start()
    {
        if (_disposed)
            return;

        Refresh();
        _timer ??= CreateTimer();
        _timer.Start();
    }

    /// <summary>Stops the timer. Call when the window is closed or minimised.</summary>
    public void Stop() => _timer?.Stop();

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        return timer;
    }

    /// <summary>Reads the latest state and updates every property. Public to the tests through InternalsVisibleTo.</summary>
    internal void Refresh()
    {
        if (_disposed)
            return;

        try
        {
            var norad = _tracking.FocusedNoradId;
            if (string.IsNullOrWhiteSpace(norad))
            {
                ShowNothing(EarthViewTextBuilder.KeyEmptyNoSatellite);
                return;
            }

            // Read together, so the position and the time it was propagated for always belong to one tick.
            var (states, snapshotUtc) = _tracking.GetSnapshotWithUtc();
            var state = states
                .FirstOrDefault(s => string.Equals(s.NoradId, norad, StringComparison.Ordinal));
            if (state is null)
            {
                ShowNothing(EarthViewTextBuilder.KeyEmptyNoPosition);
                return;
            }

            // The time the position was propagated for, which already includes the map offset and any GPS
            // time correction. Falls back to the clock only before the first snapshot has been published.
            var displayUtc = snapshotUtc > DateTime.MinValue ? snapshotUtc : _utcNow() + _tracking.MapTimeOffset;
            var settings = _settings.Current;
            var next = EarthViewFrameBuilder.Build(state, settings.GroundStation, settings.MinimumElevationDeg, displayUtc);
            if (next is null)
            {
                // The state cannot be drawn (no usable altitude). Keep the last good frame for the
                // same satellite so the globe does not flicker, and say so once in the log.
                if (_loggedUnusableState.Add(state.NoradId))
                    Log.Warning("Earth View cannot draw {Norad}: altitude {Altitude} km is not usable", state.NoradId, state.AltitudeKm);

                if (Frame is null || !string.Equals(Frame.NoradId, state.NoradId, StringComparison.Ordinal))
                    ShowNothing(EarthViewTextBuilder.KeyEmptyNoPosition);

                return;
            }

            Publish(next);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Earth View refresh failed");
        }
    }

    private void Publish(EarthViewFrame next)
    {
        if (next.NeedsRedraw(Frame))
            Frame = next;

        HasSatellite = true;
        EmptyMessage = "";
        StatusSections = EarthViewTextBuilder.Sections(_l, next);
        StatusText = EarthViewTextBuilder.Status(_l, next);

        if (next.IsMaterialChange(_summaryFrame))
        {
            _summaryFrame = next;
            AccessibleName = EarthViewTextBuilder.AccessibleName(_l, next);
            AccessibleSummary = EarthViewTextBuilder.AccessibleSummary(_l, next);
        }
    }

    private void ShowNothing(string messageKey)
    {
        Frame = null;
        _summaryFrame = null;
        HasSatellite = false;
        StatusSections = [];
        StatusText = "";
        EmptyMessage = _l.Get(messageKey);
        AccessibleName = EarthViewTextBuilder.AccessibleName(_l, null);
        AccessibleSummary = EmptyMessage;
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
