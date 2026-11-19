using OscarWatch.Core.Models;

namespace OscarWatch.Core.Eme;

/// <summary>Builds an <see cref="EmeFrame"/>. Pure: the same inputs always give the same frame.</summary>
public static class EmeFrameBuilder
{
    /// <summary>
    /// The frame at <paramref name="utc"/>, or null when the Moon's position cannot be worked out (a station that
    /// is not a number, for example). <paramref name="mask"/> is applied only when the settings ask for it.
    /// When <paramref name="frequencyIsUsable"/> is false (the operator's entry is not a valid frequency) no
    /// Doppler or absolute path loss is worked out.
    /// </summary>
    public static EmeFrame? Build(
        DateTime utc, StationPosition station, EmeSettings eme, HorizonMask? mask, MoonNextEvent? next,
        bool frequencyIsUsable = true)
    {
        var sample = MoonEphemeris.At(utc, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        if (sample is null)
            return null;

        var band = eme.SelectedBandId;
        var mhz = eme.FrequencyMHzFor(band);
        double? frequency = frequencyIsUsable && EmeLinkMath.IsValidFrequency(mhz) ? mhz : null;
        var beamwidth = eme.BeamwidthFor(band);

        var rule = new MoonVisibilityRule(
            eme.ElevationLimitDeg, eme.MaximumElevationDeg, eme.UseHorizonMask ? mask : null);
        var extra = EmeLinkMath.ExtraPathLossDb(sample.DistanceKm);

        return new EmeFrame(
            sample.TimeUtc,
            sample,
            rule.IsUp(sample),
            eme.ElevationLimitDeg,
            band,
            frequency,
            EmeLinkMath.EchoDelaySeconds(sample.DistanceKm),
            frequency is { } f ? EmeLinkMath.OwnEchoShiftHz(f * 1e6, sample.RangeRateKmPerS) : null,
            extra,
            EmeLinkMath.ClassOf(extra),
            frequency is { } g ? EmeLinkMath.AbsolutePathLossDb(g * 1e6, sample.DistanceKm) : null,
            eme.IsCircular(band),
            beamwidth,
            MoonSkyContext.At(utc, station, frequency ?? mhz, beamwidth, sample.DistanceKm),
            next);
    }
}
