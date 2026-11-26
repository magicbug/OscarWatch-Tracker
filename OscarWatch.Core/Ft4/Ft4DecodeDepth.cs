namespace OscarWatch.Core.Ft4;

/// <summary>
/// Deep FT4 decode near the horizon, where the downlink is weakest.
/// Mid-pass stays on the fast budget so a reply still makes the next slot.
/// </summary>
public static class Ft4DecodeDepth
{
    /// <summary>Deep decode while the satellite is up and below this elevation.</summary>
    public const double HorizonElevationDeg = 20;

    public static bool UseDeep(double? elevationDeg) =>
        elevationDeg is >= 0 and < HorizonElevationDeg;

    /// <summary>Also decode the full receive slot while the satellite is up and below this elevation.</summary>
    public const double FullSlotElevationDeg = 5;

    /// <summary>
    /// The first few degrees: a weak or slightly late burst can miss the 6 s early decode,
    /// so receive slots get a second pass over the whole 7.5 s capture.
    /// </summary>
    public static bool UseFullSlotDecode(double? elevationDeg) =>
        elevationDeg is >= 0 and < FullSlotElevationDeg;

    /// <summary>
    /// Hinted replies are only tried while the satellite is still up.
    /// Below the horizon those same hints turn noise into a plausible report.
    /// </summary>
    public static bool UseApriori(double? elevationDeg) =>
        elevationDeg is >= 0;

    /// <summary>
    /// Earliest raw slot-relative DT still treated as a satellite copy. Up to 0.9 s
    /// early on the WSJT-X display scale (raw − 0.5 s offset) is accepted so
    /// genuine early-timed satellite bursts are not dropped. The known R+35 false
    /// decode sat at raw −0.5 s and remains below this floor.
    /// </summary>
    public const double MinSatelliteDtSec = -0.4;

    /// <summary>
    /// SNR estimator floor. A hinted reply parked on this value has no measurable signal.
    /// </summary>
    public const float ApSnrFloorDb = -21f;

    /// <summary>Highest report a hint may invent. A louder one still decodes on its own.</summary>
    public const int MaxHintedReportDb = 20;

    /// <summary>A hinted reply needs a satellite DT and a signal above the SNR floor.</summary>
    public static bool IsPublishableHint(double timeSec, float snrDb) =>
        IsPlausibleSatelliteDt(timeSec) && snrDb > ApSnrFloorDb;

    /// <summary>
    /// Latest DT still treated as a satellite copy. FT4 starts 0.5 s into the
    /// slot and a satellite adds only milliseconds, so a real line stays near
    /// that. A moonbounce echo is about +2.5 s.
    /// </summary>
    public const double MaxSatelliteDtSec = 1.5;

    public static bool IsPlausibleSatelliteDt(double timeSec) =>
        timeSec is >= MinSatelliteDtSec and <= MaxSatelliteDtSec;

    /// <summary>
    /// FT4 tones start 0.5 s into the slot. OscarWatch measures DT from the start of the
    /// slot, so an on-time signal reads about +0.5 s. WSJT-X takes this 0.5 s off, so its
    /// DT of 0 means on time. This is the offset to subtract to match the WSJT-X convention.
    /// </summary>
    public const double WsjtxDtOffsetSec = 0.5;

    /// <summary>
    /// Converts an OscarWatch slot-relative DT to the display value. When
    /// <paramref name="wsjtxConvention"/> is true the 0.5 s start offset is removed so the
    /// value lines up with WSJT-X. This is display only; filtering uses the raw DT.
    /// </summary>
    public static double DisplayDt(double timeSec, bool wsjtxConvention) =>
        wsjtxConvention ? timeSec - WsjtxDtOffsetSec : timeSec;
}
