using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Services;

public sealed class LiveTrackerSnapshotProvider : ILiveTrackerSnapshotProvider
{
    private readonly FrequencyOverlayViewModel _frequencies;
    private readonly ILiveTrackingService _liveTracking;
    private string? _focusedNoradId;

    public LiveTrackerSnapshotProvider(
        FrequencyOverlayViewModel frequencies,
        ILiveTrackingService liveTracking)
    {
        _frequencies = frequencies;
        _liveTracking = liveTracking;
    }

    public string? FocusedNoradId
    {
        get => _focusedNoradId;
        set => _focusedNoradId = value;
    }

    public LiveTrackerSnapshot GetCurrent()
    {
        var state = ResolveFocusedState();
        if (state is null)
            return LiveTrackerSnapshot.Empty;

        var modeType = _frequencies.SelectedMode?.Type?.Trim() ?? "";
        var elevationDeg = state.LookAngles?.ElevationDeg;
        var rangeRateKmPerSec = state.LookAngles?.RangeRateKmPerSec;

        var context = _frequencies.TryBuildRigTrackingContext(state);
        if (context is null)
        {
            // Prefer the focused track name. The overlay SatelliteName can lag after a focus
            // change and must not stamp the previous satellite onto a QSO.
            var name = state.Name;
            return string.IsNullOrWhiteSpace(name)
                ? LiveTrackerSnapshot.Empty
                : new LiveTrackerSnapshot(name.Trim(), "", "", 0, 0, "", "", modeType, elevationDeg, rangeRateKmPerSec);
        }

        var update = CloudlogRadioMapper.TryCreate(
            context.TrackState.Name,
            context.Mode,
            context.Corrected,
            context.CwUplink,
            context.CwKeepSidebandDownlink);
        if (update is null)
            return LiveTrackerSnapshot.Empty;

        return new LiveTrackerSnapshot(
            update.SatelliteName,
            update.UplinkMode,
            update.DownlinkMode,
            update.UplinkHz,
            update.DownlinkHz,
            AdifBandHelper.FromHz(update.UplinkHz),
            AdifBandHelper.FromHz(update.DownlinkHz),
            string.IsNullOrWhiteSpace(modeType) ? context.Mode.Type.Trim() : modeType,
            elevationDeg,
            rangeRateKmPerSec);
    }

    private SatelliteTrackState? ResolveFocusedState()
    {
        if (string.IsNullOrWhiteSpace(_focusedNoradId))
            return null;

        var snapshot = _liveTracking.GetSnapshot();
        return snapshot.FirstOrDefault(s =>
            string.Equals(s.NoradId, _focusedNoradId, StringComparison.Ordinal));
    }
}
