using System.Globalization;
using OscarWatch.Core.Geo;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

/// <summary>
/// Builds the readable text for the Earth View from a frame, using localised strings. The text carries
/// everything the picture shows (whether the station is inside the footprint, elevation, sunlit state),
/// so nothing depends on colour or on seeing the globe.
/// </summary>
internal static class EarthViewTextBuilder
{
    public const string KeyWindowTitle = "Window.EarthView.Title";
    public const string KeyEmptyNoSatellite = "EarthView.Empty.NoSatellite";
    public const string KeyEmptyNoPosition = "EarthView.Empty.NoPosition";
    public const string KeySectionSatellite = "EarthView.Section.Satellite";
    public const string KeySectionStation = "EarthView.Section.Station";
    public const string KeySectionCoverage = "EarthView.Section.Coverage";
    public const string KeyLabelAltitude = "EarthView.Label.Altitude";
    public const string KeyLabelTime = "EarthView.Label.Time";
    public const string KeyLabelLighting = "EarthView.Label.Lighting";
    public const string KeyLabelAzimuth = "EarthView.Label.Azimuth";
    public const string KeyLabelElevation = "EarthView.Label.Elevation";
    public const string KeyLabelRange = "EarthView.Label.Range";
    public const string KeyLabelFootprint = "EarthView.Label.Footprint";
    public const string KeyLabelMinElevation = "EarthView.Label.MinElevation";
    public const string KeyValueKilometres = "EarthView.Value.Kilometres";
    public const string KeyValueDegrees = "EarthView.Value.Degrees";
    public const string KeyStatusTime = "EarthView.Status.Time";
    public const string KeyStatusBelowHorizon = "EarthView.Status.BelowHorizon";
    public const string KeyStatusAboveHorizon = "EarthView.Status.AboveHorizon";
    public const string KeyStatusInsideMinElevation = "EarthView.Status.InsideMinElevation";
    public const string KeyStatusOutsideMinElevation = "EarthView.Status.OutsideMinElevation";
    public const string KeyStatusSunlit = "EarthView.Status.Sunlit";
    public const string KeyStatusEclipse = "EarthView.Status.Eclipse";
    public const string KeyAccessibleName = "EarthView.Accessible.Name";
    public const string KeyAccessibleUsable = "EarthView.Accessible.Summary.Usable";
    public const string KeyAccessibleAboveHorizon = "EarthView.Accessible.Summary.AboveHorizon";
    public const string KeyAccessibleBelowHorizon = "EarthView.Accessible.Summary.BelowHorizon";
    public const string KeyAccessibleNoAngles = "EarthView.Accessible.Summary.NoAngles";

    /// <summary>
    /// The facts for the panel beside the globe, in sections: the satellite, your station, and how the two
    /// relate. A section with nothing to say is left out.
    /// </summary>
    public static IReadOnlyList<EarthViewStatusSection> Sections(ILocalizationService l, EarthViewFrame frame)
    {
        var satellite = new List<EarthViewStatusRow>
        {
            new(null, frame.SatelliteName),
            new(l.Get(KeyLabelAltitude), l.Get(KeyValueKilometres, frame.Camera.AltitudeKm)),
            new(l.Get(KeyLabelTime), l.Get(KeyStatusTime, frame.DisplayUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))),
            new(l.Get(KeyLabelLighting), l.Get(frame.IsSunlit ? KeyStatusSunlit : KeyStatusEclipse))
        };

        var sections = new List<EarthViewStatusSection> { new(l.Get(KeySectionSatellite), satellite) };

        if (frame.LookFromHome is { } look)
        {
            sections.Add(new EarthViewStatusSection(l.Get(KeySectionStation),
            [
                new(l.Get(KeyLabelAzimuth), l.Get(KeyValueDegrees, look.AzimuthDeg)),
                new(l.Get(KeyLabelElevation), l.Get(KeyValueDegrees, look.ElevationDeg)),
                new(l.Get(KeyLabelRange), l.Get(KeyValueKilometres, look.RangeKm))
            ]));
        }

        var coverage = new List<EarthViewStatusRow>();
        switch (frame.HomeSituation)
        {
            case EarthViewSituation.BelowHorizon:
                coverage.Add(new(l.Get(KeyLabelFootprint), l.Get(KeyStatusBelowHorizon)));
                break;
            case EarthViewSituation.AboveHorizon:
                coverage.Add(new(l.Get(KeyLabelFootprint), l.Get(KeyStatusAboveHorizon)));
                coverage.Add(new(l.Get(KeyLabelMinElevation), l.Get(KeyStatusOutsideMinElevation, frame.MinimumElevationDeg)));
                break;
            case EarthViewSituation.Usable:
                coverage.Add(new(l.Get(KeyLabelFootprint), l.Get(KeyStatusAboveHorizon)));
                coverage.Add(new(l.Get(KeyLabelMinElevation), l.Get(KeyStatusInsideMinElevation, frame.MinimumElevationDeg)));
                break;
        }

        if (coverage.Count > 0)
            sections.Add(new EarthViewStatusSection(l.Get(KeySectionCoverage), coverage));

        return sections;
    }

    /// <summary>The same facts as plain text, one line per row, with each heading on its own line.</summary>
    public static string Status(ILocalizationService l, EarthViewFrame frame) =>
        string.Join("\n", Sections(l, frame).SelectMany(section => section.Rows
            .Select(row => row.Label is null ? row.Value : $"{row.Label}: {row.Value}")
            .Prepend(section.Heading)));

    /// <summary>
    /// The control's accessible name: short, and names the satellite. With no satellite it is just the window
    /// title, so it never reads "Earth View," with nothing after the comma.
    /// </summary>
    public static string AccessibleName(ILocalizationService l, EarthViewFrame? frame) =>
        frame is null || string.IsNullOrWhiteSpace(frame.SatelliteName)
            ? l.Get(KeyWindowTitle)
            : l.Get(KeyAccessibleName, frame.SatelliteName);

    /// <summary>
    /// The control's accessible help text: the satellite, where it is in the sky, and whether the
    /// station is inside the coverage. Angles are rounded to whole degrees for speech.
    /// </summary>
    public static string AccessibleSummary(ILocalizationService l, EarthViewFrame frame)
    {
        if (frame.HomeSituation == EarthViewSituation.BelowHorizon)
            return l.Get(KeyAccessibleBelowHorizon, frame.SatelliteName);

        if (frame.LookFromHome is not { } look)
            return l.Get(KeyAccessibleNoAngles, frame.SatelliteName);

        var key = frame.HomeSituation == EarthViewSituation.Usable ? KeyAccessibleUsable : KeyAccessibleAboveHorizon;
        return l.Get(
            key,
            frame.SatelliteName,
            Math.Round(look.ElevationDeg),
            Math.Round(look.AzimuthDeg),
            frame.MinimumElevationDeg);
    }
}

/// <summary>One part of the status panel: a heading and the rows under it.</summary>
public sealed record EarthViewStatusSection(string Heading, IReadOnlyList<EarthViewStatusRow> Rows);

/// <summary>One fact in the status panel. With no label the value spans the whole row (the satellite's name).</summary>
public sealed record EarthViewStatusRow(string? Label, string Value)
{
    /// <summary>The grid column the value starts in: the first when there is no label.</summary>
    public int ValueColumn => Label is null ? 0 : 1;

    /// <summary>How many grid columns the value spans.</summary>
    public int ValueColumnSpan => Label is null ? 2 : 1;
}
