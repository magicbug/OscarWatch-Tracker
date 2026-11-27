using OscarWatch.Core.Models;

namespace OscarWatch.Core.Display;

public enum PassbandZone
{
    InBand,
    NearEdge,
    OutOfBand
}

/// <summary>
/// A sibling mode (e.g. FT4 watering hole or CW TLM beacon) plotted on a transponder passband bar.
/// </summary>
public sealed record PassbandSpot(
    string Label,
    double DownlinkKHz,
    double NormalizedPosition,
    double? SpanNormalized,
    bool IsBeacon);

/// <summary>
/// Snapshot of the active linear transponder passband and the current satellite-frame tuning position.
/// </summary>
public sealed record PassbandIndicatorState(
    double DownlinkMinKHz,
    double DownlinkMaxKHz,
    double? UplinkMinKHz,
    double? UplinkMaxKHz,
    double SatelliteDownlinkKHz,
    double? SatelliteUplinkKHz,
    bool IsInverting,
    double WidthKHz,
    double DownlinkMarkerPosition,
    PassbandZone Zone,
    double OffsetFromCentreKHz,
    double OutOfBandDeltaKHz,
    IReadOnlyList<PassbandSpot> Spots);

public static class PassbandIndicatorCalculator
{
    /// <summary>Fraction of the total bar width reserved for each out-of-band gutter (left and right).</summary>
    public const double GutterFraction = 0.12;

    /// <summary>Fraction of the total bar width occupied by the active passband.</summary>
    public const double PassbandFraction = 1.0 - (2.0 * GutterFraction);

    /// <summary>Fraction of passband width inside either edge considered "near edge" (5% = 3 kHz on a 60 kHz passband).</summary>
    private const double NearEdgeFraction = 0.05;

    /// <summary>Nominal width drawn for an FT4 or FT8 watering hole (kHz).</summary>
    private const double DigitalSpotWidthKHz = 2.5;

    /// <summary>
    /// Resolves passband bounds for <paramref name="selectedMode"/>:
    /// 1. Mode-level bounds if present (e.g. multi-transponder satellites such as AO-07).
    /// 2. Satellite-level bounds on <paramref name="entry"/> when <paramref name="selectedMode"/> is not beacon-only
    ///    and its nominal downlink lies inside the satellite's passband.
    /// 3. An enclosing sibling mode's passband bounds if present.
    /// </summary>
    public static bool TryResolveBounds(
        SatelliteRadioEntry? entry,
        SatelliteTransponderMode? selectedMode,
        IReadOnlyList<SatelliteTransponderMode> availableModes,
        out double dlMin,
        out double dlMax,
        out double? ulMin,
        out double? ulMax,
        out bool isInverting)
    {
        dlMin = 0;
        dlMax = 0;
        ulMin = null;
        ulMax = null;
        isInverting = false;

        if (selectedMode is null)
            return false;

        if (selectedMode.HasDownlinkPassband)
        {
            dlMin = selectedMode.DownlinkMinKHz!.Value;
            dlMax = selectedMode.DownlinkMaxKHz!.Value;
            ulMin = selectedMode.HasUplinkPassband ? selectedMode.UplinkMinKHz : null;
            ulMax = selectedMode.HasUplinkPassband ? selectedMode.UplinkMaxKHz : null;
            isInverting = selectedMode.DopplerCorrection == DopplerCorrection.Reverse;
            return true;
        }

        if (selectedMode.IsBeaconOnly || selectedMode.DownlinkKHz <= 0)
            return false;

        if (entry is { HasDownlinkPassband: true }
            && selectedMode.DownlinkKHz >= entry.DownlinkMinKHz!.Value
            && selectedMode.DownlinkKHz <= entry.DownlinkMaxKHz!.Value)
        {
            dlMin = entry.DownlinkMinKHz!.Value;
            dlMax = entry.DownlinkMaxKHz!.Value;
            ulMin = entry.HasUplinkPassband ? entry.UplinkMinKHz : null;
            ulMax = entry.HasUplinkPassband ? entry.UplinkMaxKHz : null;
            isInverting = selectedMode.DopplerCorrection == DopplerCorrection.Reverse;
            return true;
        }

        foreach (var candidate in availableModes)
        {
            if (ReferenceEquals(candidate, selectedMode) || !candidate.HasDownlinkPassband)
                continue;

            if (selectedMode.DownlinkKHz >= candidate.DownlinkMinKHz!.Value
                && selectedMode.DownlinkKHz <= candidate.DownlinkMaxKHz!.Value)
            {
                dlMin = candidate.DownlinkMinKHz!.Value;
                dlMax = candidate.DownlinkMaxKHz!.Value;
                ulMin = candidate.HasUplinkPassband ? candidate.UplinkMinKHz : null;
                ulMax = candidate.HasUplinkPassband ? candidate.UplinkMaxKHz : null;
                isInverting = candidate.DopplerCorrection == DopplerCorrection.Reverse;
                return true;
            }
        }

        return false;
    }

    public static PassbandIndicatorState? Build(
        SatelliteRadioEntry? entry,
        SatelliteTransponderMode? selectedMode,
        IReadOnlyList<SatelliteTransponderMode> availableModes,
        double satelliteDownlinkKHz,
        double? satelliteUplinkKHz)
    {
        if (satelliteDownlinkKHz <= 0)
            return null;

        if (!TryResolveBounds(
                entry,
                selectedMode,
                availableModes,
                out var dlMin,
                out var dlMax,
                out var ulMin,
                out var ulMax,
                out var isInverting))
        {
            return null;
        }

        var width = dlMax - dlMin;
        if (width <= 0)
            return null;

        var dlNorm = (satelliteDownlinkKHz - dlMin) / width;
        var markerPos = Math.Clamp(GutterFraction + (dlNorm * PassbandFraction), 0.02, 0.98);

        var centre = dlMin + (width / 2.0);
        var offsetFromCentre = satelliteDownlinkKHz - centre;

        PassbandZone zone;
        double outOfBandDelta;
        if (satelliteDownlinkKHz < dlMin)
        {
            zone = PassbandZone.OutOfBand;
            outOfBandDelta = satelliteDownlinkKHz - dlMin;
        }
        else if (satelliteDownlinkKHz > dlMax)
        {
            zone = PassbandZone.OutOfBand;
            outOfBandDelta = satelliteDownlinkKHz - dlMax;
        }
        else
        {
            outOfBandDelta = 0;
            var edgeMargin = width * NearEdgeFraction;
            var nearLow = (satelliteDownlinkKHz - dlMin) < edgeMargin;
            var nearHigh = (dlMax - satelliteDownlinkKHz) < edgeMargin;
            zone = (nearLow || nearHigh) ? PassbandZone.NearEdge : PassbandZone.InBand;
        }

        var gutterKHz = width * (GutterFraction / PassbandFraction);
        var visibleMin = dlMin - gutterKHz;
        var visibleMax = dlMax + gutterKHz;

        var spots = new List<PassbandSpot>();
        foreach (var mode in availableModes)
        {
            if (mode.DownlinkKHz < visibleMin || mode.DownlinkKHz > visibleMax)
                continue;

            var isBeacon = mode.IsBeaconOnly;
            var isDigital = !isBeacon && IsDigitalSpotMode(mode);

            // Plot FT4/FT8 watering holes and beacons. Skip the voice transponder entry, which is only the default tuning point.
            if (!isBeacon && !isDigital)
                continue;

            var pos = GutterFraction + (((mode.DownlinkKHz - dlMin) / width) * PassbandFraction);
            if (pos < 0.02 || pos > 0.98)
                continue;

            double? span = isDigital
                ? Math.Clamp((DigitalSpotWidthKHz / width) * PassbandFraction, 0.015, 0.20)
                : null;

            spots.Add(new PassbandSpot(
                Label: mode.DisplayLabel,
                DownlinkKHz: mode.DownlinkKHz,
                NormalizedPosition: pos,
                SpanNormalized: span,
                IsBeacon: isBeacon));
        }

        spots.Sort((a, b) => a.DownlinkKHz.CompareTo(b.DownlinkKHz));

        return new PassbandIndicatorState(
            DownlinkMinKHz: dlMin,
            DownlinkMaxKHz: dlMax,
            UplinkMinKHz: ulMin,
            UplinkMaxKHz: ulMax,
            SatelliteDownlinkKHz: satelliteDownlinkKHz,
            SatelliteUplinkKHz: satelliteUplinkKHz,
            IsInverting: isInverting,
            WidthKHz: width,
            DownlinkMarkerPosition: markerPos,
            Zone: zone,
            OffsetFromCentreKHz: offsetFromCentre,
            OutOfBandDeltaKHz: outOfBandDelta,
            Spots: spots);
    }

    /// <summary>
    /// FT4 and FT8 are narrow watering holes. Other DATA modes (packet, DATV, digipeaters) are not a 2.5 kHz slice.
    /// </summary>
    private static bool IsDigitalSpotMode(SatelliteTransponderMode mode) =>
        mode.Type.Contains("FT4", StringComparison.OrdinalIgnoreCase)
        || mode.Type.Contains("FT8", StringComparison.OrdinalIgnoreCase);
}
