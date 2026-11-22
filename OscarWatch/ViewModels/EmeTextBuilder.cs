using System.Text;
using OscarWatch.Core.Eme;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

/// <summary>
/// Builds the readable text for the Moon window from a frame, using localised strings. The text carries
/// everything the window shows, so nothing depends on colour: the Moon's state, the path loss class and the
/// flags are all words.
/// </summary>
internal static class EmeTextBuilder
{
    public const string KeyWindowTitle = "Window.Moon.Title";
    public const string KeyEmptyNoPosition = "Moon.Empty.NoPosition";
    public const string KeyFrequencyInvalid = "Moon.FrequencyInvalid";

    public const string KeySectionPosition = "Moon.Section.Position";
    public const string KeySectionEcho = "Moon.Section.Echo";
    public const string KeySectionMoon = "Moon.Section.Moon";
    public const string KeySectionVisibility = "Moon.Section.Visibility";

    public const string KeyLabelAzimuth = "Moon.Label.Azimuth";
    public const string KeyLabelElevation = "Moon.Label.Elevation";
    public const string KeyLabelHourAngle = "Moon.Label.HourAngle";
    public const string KeyLabelGreenwichHourAngle = "Moon.Label.GreenwichHourAngle";
    public const string KeyLabelDeclination = "Moon.Label.Declination";
    public const string KeyLabelSublunar = "Moon.Label.Sublunar";
    public const string KeyLabelDistance = "Moon.Label.Distance";
    public const string KeyLabelRangeRate = "Moon.Label.RangeRate";
    public const string KeyLabelEchoDelay = "Moon.Label.EchoDelay";
    public const string KeyLabelOwnEcho = "Moon.Label.OwnEcho";
    public const string KeyLabelExtraLoss = "Moon.Label.ExtraLoss";
    public const string KeyLabelAbsoluteLoss = "Moon.Label.AbsoluteLoss";
    public const string KeyLabelPhase = "Moon.Label.Phase";
    public const string KeyLabelDiameter = "Moon.Label.Diameter";
    public const string KeyLabelNextPerigee = "Moon.Label.NextPerigee";
    public const string KeyLabelNextApogee = "Moon.Label.NextApogee";
    public const string KeyLabelLastApsis = "Moon.Label.LastApsis";
    public const string KeyLabelSunSeparation = "Moon.Label.SunSeparation";
    public const string KeyLabelGalacticSeparation = "Moon.Label.GalacticSeparation";
    public const string KeyLabelStatus = "Moon.Label.Status";
    public const string KeyLabelNext = "Moon.Label.Next";
    public const string KeyLabelTime = "Moon.Label.Time";

    public const string KeyValueDegrees = "Moon.Value.Degrees";
    public const string KeyValuePosition = "Moon.Value.Position";
    public const string KeyValueKilometres = "Moon.Value.Kilometres";
    public const string KeyValueRangeRate = "Moon.Value.RangeRate";
    public const string KeyValueSeconds = "Moon.Value.Seconds";
    public const string KeyValueHertz = "Moon.Value.Hertz";
    public const string KeyValueDecibels = "Moon.Value.Decibels";
    public const string KeyValueExtraLoss = "Moon.Value.ExtraLoss";
    public const string KeyValuePhase = "Moon.Value.Phase";
    public const string KeyValueDiameter = "Moon.Value.Diameter";
    public const string KeyValueApsis = "Moon.Value.Apsis";
    public const string KeyValueLastApsis = "Moon.Value.LastApsis";
    public const string KeyValueSeparation = "Moon.Value.Separation";
    public const string KeyValueSeparationFlag = "Moon.Value.SeparationFlag";
    public const string KeyValueTimeUtc = "Moon.Value.TimeUtc";

    public const string KeyFlagSun = "Moon.Flag.Sun";
    public const string KeyFlagGalactic = "Moon.Flag.Galactic";
    public const string KeyNoteNewMoon = "Moon.Note.NewMoon";
    public const string KeyNoteFreeSpace = "Moon.Note.FreeSpace";

    public const string KeyStatusUp = "Moon.Status.Up";
    public const string KeyStatusDown = "Moon.Status.Down";
    public const string KeyShortUp = "Moon.Short.Up";
    public const string KeyShortDown = "Moon.Short.Down";
    public const string KeyNextRise = "Moon.Next.Rise";
    public const string KeyNextSet = "Moon.Next.Set";
    public const string KeyNextNone = "Moon.Next.None";

    public const string KeyApsisPerigee = "Moon.Apsis.Perigee";
    public const string KeyApsisApogee = "Moon.Apsis.Apogee";

    public const string KeyClassGood = "Moon.Class.Good";
    public const string KeyClassFair = "Moon.Class.Fair";
    public const string KeyClassPoor = "Moon.Class.Poor";
    public const string KeyClassBad = "Moon.Class.Bad";

    public const string KeyPhaseNew = "Moon.Phase.New";
    public const string KeyPhaseWaxingCrescent = "Moon.Phase.WaxingCrescent";
    public const string KeyPhaseFirstQuarter = "Moon.Phase.FirstQuarter";
    public const string KeyPhaseWaxingGibbous = "Moon.Phase.WaxingGibbous";
    public const string KeyPhaseFull = "Moon.Phase.Full";
    public const string KeyPhaseWaningGibbous = "Moon.Phase.WaningGibbous";
    public const string KeyPhaseLastQuarter = "Moon.Phase.LastQuarter";
    public const string KeyPhaseWaningCrescent = "Moon.Phase.WaningCrescent";

    public const string KeyAccessibleName = "Moon.Accessible.Name";
    public const string KeyAccessibleSummaryUp = "Moon.Accessible.Summary.Up";
    public const string KeyAccessibleSummaryDown = "Moon.Accessible.Summary.Down";
    public const string KeyCopyHeader = "Moon.Copy.Header";

    /// <summary>The words for a path loss class: "good", "fair", "poor" or "bad".</summary>
    public static string ClassWord(ILocalizationService l, PathLossClass c) => l.Get(c switch
    {
        PathLossClass.Good => KeyClassGood,
        PathLossClass.Fair => KeyClassFair,
        PathLossClass.Poor => KeyClassPoor,
        _ => KeyClassBad
    });

    public static string PhaseWord(ILocalizationService l, MoonPhaseName p) => l.Get(p switch
    {
        MoonPhaseName.New => KeyPhaseNew,
        MoonPhaseName.WaxingCrescent => KeyPhaseWaxingCrescent,
        MoonPhaseName.FirstQuarter => KeyPhaseFirstQuarter,
        MoonPhaseName.WaxingGibbous => KeyPhaseWaxingGibbous,
        MoonPhaseName.Full => KeyPhaseFull,
        MoonPhaseName.WaningGibbous => KeyPhaseWaningGibbous,
        MoonPhaseName.LastQuarter => KeyPhaseLastQuarter,
        _ => KeyPhaseWaningCrescent
    });

    /// <summary>"Up (at or above 0.0°)" or "Below the limit of 0.0°".</summary>
    public static string StatusText(ILocalizationService l, EmeFrame f) =>
        f.IsUp ? l.Get(KeyStatusUp, f.ElevationLimitDeg) : l.Get(KeyStatusDown, f.ElevationLimitDeg);

    public static string NextText(ILocalizationService l, EmeFrame f, Func<DateTime, string> formatTime) =>
        f.Next is null
            ? l.Get(KeyNextNone)
            : l.Get(f.Next.IsSet ? KeyNextSet : KeyNextRise, formatTime(f.Next.TimeUtc));

    /// <summary>The facts in sections: where the Moon is, the echo and the path, the Moon today, and rise and set.</summary>
    public static IReadOnlyList<EarthViewStatusSection> Sections(
        ILocalizationService l, EmeFrame f, Func<DateTime, string> formatTime)
    {
        var s = f.Sample;
        string Deg(double v) => l.Get(KeyValueDegrees, v);

        var position = new List<EarthViewStatusRow>
        {
            new(l.Get(KeyLabelAzimuth), Deg(s.AzimuthDeg)),
            new(l.Get(KeyLabelElevation), Deg(s.ElevationDeg)),
            new(l.Get(KeyLabelHourAngle), Deg(s.LocalHourAngleDeg)),
            new(l.Get(KeyLabelGreenwichHourAngle), Deg(s.GreenwichHourAngleDeg)),
            new(l.Get(KeyLabelDeclination), Deg(s.DeclinationDeg)),
            new(l.Get(KeyLabelSublunar), l.Get(KeyValuePosition, s.SublunarLatitudeDeg, s.SublunarLongitudeDeg))
        };

        var echo = new List<EarthViewStatusRow>
        {
            new(l.Get(KeyLabelDistance), l.Get(KeyValueKilometres, s.DistanceKm)),
            new(l.Get(KeyLabelRangeRate), l.Get(KeyValueRangeRate, s.RangeRateKmPerS)),
            new(l.Get(KeyLabelEchoDelay), l.Get(KeyValueSeconds, f.EchoDelaySeconds))
        };
        if (f.OwnEchoShiftHz is { } shift)
            echo.Add(new(l.Get(KeyLabelOwnEcho), l.Get(KeyValueHertz, shift)));
        echo.Add(new(l.Get(KeyLabelExtraLoss), l.Get(KeyValueExtraLoss, f.ExtraPathLossDb, ClassWord(l, f.LossClass))));
        if (f.AbsolutePathLossDb is { } absolute)
            echo.Add(new(l.Get(KeyLabelAbsoluteLoss), l.Get(KeyValueDecibels, absolute)));
        echo.Add(new(null, l.Get(KeyNoteFreeSpace)));

        var sections = new List<EarthViewStatusSection>
        {
            new(l.Get(KeySectionPosition), position),
            new(l.Get(KeySectionEcho), echo)
        };

        if (f.Sky is { } sky)
        {
            var moon = new List<EarthViewStatusRow>
            {
                new(l.Get(KeyLabelPhase), l.Get(KeyValuePhase, PhaseWord(l, sky.Phase), sky.IlluminatedPercent)),
                new(l.Get(KeyLabelDiameter), l.Get(KeyValueDiameter, sky.ApparentDiameterArcmin)),
                new(l.Get(KeyLabelNextPerigee), l.Get(KeyValueApsis, formatTime(sky.NextPerigee.Utc), sky.NextPerigee.DistanceKm)),
                new(l.Get(KeyLabelNextApogee), l.Get(KeyValueApsis, formatTime(sky.NextApogee.Utc), sky.NextApogee.DistanceKm))
            };
            if (double.IsFinite(sky.DaysSinceLastApsis))
                moon.Add(new(l.Get(KeyLabelLastApsis),
                    l.Get(KeyValueLastApsis, l.Get(sky.LastApsisWasPerigee ? KeyApsisPerigee : KeyApsisApogee), sky.DaysSinceLastApsis)));
            moon.Add(new(l.Get(KeyLabelSunSeparation), sky.SunTooClose
                ? l.Get(KeyValueSeparationFlag, sky.SunSeparationDeg, l.Get(KeyFlagSun))
                : l.Get(KeyValueSeparation, sky.SunSeparationDeg)));
            if (sky.GalacticCentreSeparationDeg is { } galactic)
                moon.Add(new(l.Get(KeyLabelGalacticSeparation), sky.GalacticCentreTooClose
                    ? l.Get(KeyValueSeparationFlag, galactic, l.Get(KeyFlagGalactic))
                    : l.Get(KeyValueSeparation, galactic)));
            if (sky.Phase == MoonPhaseName.New)
                moon.Add(new(null, l.Get(KeyNoteNewMoon)));
            sections.Add(new(l.Get(KeySectionMoon), moon));
        }

        sections.Add(new(l.Get(KeySectionVisibility),
        [
            new(l.Get(KeyLabelStatus), StatusText(l, f)),
            new(l.Get(KeyLabelNext), NextText(l, f, formatTime)),
            new(l.Get(KeyLabelTime), formatTime(f.TimeUtc))
        ]));
        return sections;
    }

    public static string AccessibleName(ILocalizationService l) => l.Get(KeyAccessibleName);

    /// <summary>One sentence for a screen reader: up or down, where, and how good the path is.</summary>
    public static string AccessibleSummary(ILocalizationService l, EmeFrame f) =>
        l.Get(f.IsUp ? KeyAccessibleSummaryUp : KeyAccessibleSummaryDown,
            f.Sample.AzimuthDeg, f.Sample.ElevationDeg, f.ExtraPathLossDb, ClassWord(l, f.LossClass));

    /// <summary>The window as plain text, one line per fact, for pasting into a message to another operator.</summary>
    public static string PlainText(ILocalizationService l, EmeFrame f, Func<DateTime, string> formatTime)
    {
        var sb = new StringBuilder();
        sb.AppendLine(l.Get(KeyCopyHeader, formatTime(f.TimeUtc)));
        foreach (var section in Sections(l, f, formatTime))
        {
            sb.AppendLine();
            sb.AppendLine(section.Heading);
            foreach (var row in section.Rows)
                sb.AppendLine(row.Label is null ? row.Value : $"{row.Label}: {row.Value}");
        }
        return sb.ToString().TrimEnd();
    }
}
