using OscarWatch.Core.Dxcc;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Services;

/// <summary>Contact details from a digital mode window (FT4, Terminal) before live tracking context is added.</summary>
public sealed record LiveQsoEntry(
    DateTime QsoUtc,
    string Call,
    string RstSent,
    string RstRcvd,
    string GridSquare,
    string Mode,
    string ModeRx,
    string Comment);

/// <summary>
/// Saves a live contact to the default logbook with the focused satellite and frequencies,
/// queues the Cloudlog upload when enabled, publishes it on Satellite Link, and
/// sends it on the WSJT-X UDP feed when that option is on.
/// </summary>
public sealed class LiveQsoLogger
{
    private readonly ISettingsService _settings;
    private readonly IQsoLogbookRepository _logbook;
    private readonly ICloudlogQsoUploadService _cloudlogUpload;
    private readonly ISatelliteLinkBroadcastService _satelliteLink;
    private readonly IWsjtxUdpBroadcastService _wsjtxUdp;
    private readonly ILiveTrackerSnapshotProvider _snapshot;
    private readonly ILiveTrackingService _tracking;
    private readonly FrequencyOverlayViewModel _frequencies;
    private readonly ILocalizationService _l;
    private readonly IDxccLookupService _dxccLookup;

    public LiveQsoLogger(
        ISettingsService settings,
        IQsoLogbookRepository logbook,
        ICloudlogQsoUploadService cloudlogUpload,
        ISatelliteLinkBroadcastService satelliteLink,
        IWsjtxUdpBroadcastService wsjtxUdp,
        ILiveTrackerSnapshotProvider snapshot,
        ILiveTrackingService tracking,
        FrequencyOverlayViewModel frequencies,
        ILocalizationService localization,
        IDxccLookupService dxccLookup)
    {
        _settings = settings;
        _logbook = logbook;
        _cloudlogUpload = cloudlogUpload;
        _satelliteLink = satelliteLink;
        _wsjtxUdp = wsjtxUdp;
        _snapshot = snapshot;
        _tracking = tracking;
        _frequencies = frequencies;
        _l = localization;
        _dxccLookup = dxccLookup;
    }

    public async Task<QsoRecord> LogAsync(LiveQsoEntry entry)
    {
        var station = _settings.Current.GroundStation;
        var book = await _logbook.GetOrCreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = _l.Get("Logbook.DefaultName"),
            MyGridSquare = station.GridSquare
        }).ConfigureAwait(false);

        var snap = _snapshot.GetCurrent();
        var cloudlogUpload = book.CloudlogAutoUpload && book.CloudlogStationProfileId.HasValue
            ? CloudlogUploadStatus.Pending
            : CloudlogUploadStatus.None;
        ResolveDxccFields(entry.Call, out var dxcc, out var country);
        var record = await _logbook.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = book.Id,
            QsoUtc = entry.QsoUtc,
            Call = entry.Call,
            RstSent = entry.RstSent,
            RstRcvd = entry.RstRcvd,
            GridSquare = entry.GridSquare,
            SatName = snap.IsAvailable ? snap.SatelliteName : _frequencies.SatelliteName,
            Mode = entry.Mode,
            ModeRx = entry.ModeRx,
            FreqHz = snap.IsAvailable ? snap.UplinkHz : 0,
            FreqRxHz = snap.IsAvailable ? snap.DownlinkHz : 0,
            Band = snap.IsAvailable ? snap.Band : "",
            BandRx = snap.IsAvailable ? snap.BandRx : "",
            PropMode = "SAT",
            Comment = entry.Comment,
            Dxcc = dxcc,
            Country = country,
            PotaRef = book.PotaRef,
            SotaRef = book.SotaRef,
            WwffRef = book.WwffRef,
            CloudlogUploadStatus = cloudlogUpload
        }).ConfigureAwait(false);

        if (cloudlogUpload == CloudlogUploadStatus.Pending)
            await _cloudlogUpload.QueueUploadIfEnabledAsync(record.Id).ConfigureAwait(false);

        _satelliteLink.PublishQso(record, book, SatelliteLinkQsoEventKind.Logged, _tracking.FocusedNoradId);
        _wsjtxUdp.PublishLoggedQso(record, book);
        return record;
    }

    private void ResolveDxccFields(string call, out int? dxcc, out string country)
    {
        if (_dxccLookup.TryResolve(call, out var match))
        {
            dxcc = match.Dxcc;
            country = match.Country;
            return;
        }

        dxcc = null;
        country = "";
    }
}
