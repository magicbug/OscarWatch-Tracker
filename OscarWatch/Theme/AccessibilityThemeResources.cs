using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace OscarWatch.Theme;

/// <summary>
/// Theme-aware brushes that meet contrast targets in light and dark UI.
/// </summary>
public static class AccessibilityThemeResources
{
    public const string ThemeForegroundKey = "ThemeForegroundBrush";
    public const string ThemeSecondaryForegroundKey = "ThemeSecondaryForegroundBrush";
    public const string ThemePlaceholderForegroundKey = "ThemePlaceholderForegroundBrush";
    public const string PassHighlightKey = "PassHighlightBrush";
    public const string PassInProgressBackgroundKey = "PassInProgressBackgroundBrush";
    public const string PassRecordingBackgroundKey = "PassRecordingBackgroundBrush";
    public const string PassImminentBackgroundKey = "PassImminentBackgroundBrush";
    public const string PassLaterBackgroundKey = "PassLaterBackgroundBrush";
    public const string PassImminentBadgeBackgroundKey = "PassImminentBadgeBackgroundBrush";
    public const string PassImminentBadgeForegroundKey = "PassImminentBadgeForegroundBrush";
    public const string PassInProgressBadgeBackgroundKey = "PassInProgressBadgeBackgroundBrush";
    public const string PassInProgressBadgeForegroundKey = "PassInProgressBadgeForegroundBrush";
    public const string HamsAtGridBadgeBackgroundKey = "HamsAtGridBadgeBackgroundBrush";
    public const string PassRecordingBadgeBackgroundKey = "PassRecordingBadgeBackgroundBrush";
    public const string PassRecordingBadgeForegroundKey = "PassRecordingBadgeForegroundBrush";
    public const string StaleTleKey = "StaleTleForegroundBrush";
    public const string SunlightStatusKey = "SunlightStatusBrush";
    public const string EclipseStatusKey = "EclipseStatusBrush";
    public const string GpsOkKey = "GpsOkBrush";
    public const string GpsWarnKey = "GpsWarnBrush";
    public const string ThemeSubtlePanelBackgroundKey = "ThemeSubtlePanelBackgroundBrush";
    public const string ThemeInsetBackgroundKey = "ThemeInsetBackgroundBrush";
    public const string StandbyBannerBackgroundKey = "StandbyBannerBackgroundBrush";
    public const string StandbyBannerForegroundKey = "StandbyBannerForegroundBrush";
    public const string StandbyBannerBorderKey = "StandbyBannerBorderBrush";
    public const string StandbyActionBackgroundKey = "StandbyActionBackgroundBrush";
    public const string StandbyActionForegroundKey = "StandbyActionForegroundBrush";
    public const string StandbyActionHoverBackgroundKey = "StandbyActionHoverBackgroundBrush";
    public const string StandbyActionPressedBackgroundKey = "StandbyActionPressedBackgroundBrush";
    /// <summary>Same amber callout as standby; aliased so satellite notices stay theme-aware.</summary>
    public const string SatelliteNoticeBackgroundKey = "SatelliteNoticeBackgroundBrush";
    public const string SatelliteNoticeForegroundKey = "SatelliteNoticeForegroundBrush";
    public const string SatelliteNoticeBorderKey = "SatelliteNoticeBorderBrush";

    public static void Install()
    {
        if (Application.Current is null)
            return;

        Application.Current.ActualThemeVariantChanged += (_, _) => Apply();
        Apply();
    }

    public static void Apply()
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        var isDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        resources[ThemeForegroundKey] = new SolidColorBrush(
            isDark ? Colors.White : Color.Parse("#1A1A1A"));
        resources[ThemeSecondaryForegroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#C8CDD4") : Color.Parse("#525252"));
        resources[ThemePlaceholderForegroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#9CA3AF") : Color.Parse("#5C6370"));

        // Fluent defaults are too faint on light backgrounds for labels, hints, and watermarks.
        resources["SystemControlForegroundBaseMediumBrush"] = resources[ThemeSecondaryForegroundKey];
        resources["TextControlPlaceholderForeground"] = resources[ThemePlaceholderForegroundKey];
        resources["TextControlPlaceholderOpacity"] = 1.0;
        resources[PassHighlightKey] = new SolidColorBrush(
            isDark ? Color.Parse("#9EDE6B") : Color.Parse("#2B6E1F"));
        // Softer row fills so primary/secondary text stay crisp; accents carry status.
        resources[PassInProgressBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#1E3220") : Color.Parse("#E5F5E0"));
        resources[PassRecordingBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#1A2E26") : Color.Parse("#D8F0D4"));
        resources[PassImminentBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#322A1A") : Color.Parse("#FFF6E4"));
        resources[PassLaterBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#26282C") : Color.Parse("#F6F7F8"));
        resources[PassImminentBadgeBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#E09A20") : Color.Parse("#E89B1E"));
        resources[PassImminentBadgeForegroundKey] = new SolidColorBrush(Colors.White);
        resources[PassInProgressBadgeBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#2E7A2A") : Color.Parse("#2B6E1F"));
        resources[PassInProgressBadgeForegroundKey] = new SolidColorBrush(Colors.White);
        resources[HamsAtGridBadgeBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#2E7A2A") : Color.Parse("#2B6E1F"));
        resources[PassRecordingBadgeBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#EF4444") : Color.Parse("#DC2626"));
        resources[PassRecordingBadgeForegroundKey] = new SolidColorBrush(Colors.White);
        resources[StaleTleKey] = new SolidColorBrush(
            isDark ? Color.Parse("#FFB347") : Color.Parse("#B45309"));
        resources[SunlightStatusKey] = new SolidColorBrush(
            isDark ? Color.Parse("#F5C842") : Color.Parse("#B8860B"));
        resources[EclipseStatusKey] = new SolidColorBrush(
            isDark ? Color.Parse("#9CA3AF") : Color.Parse("#5C6370"));
        resources[GpsOkKey] = resources[PassHighlightKey];
        resources[GpsWarnKey] = resources[PassRecordingBadgeBackgroundKey];
        resources[ThemeSubtlePanelBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#26282C") : Color.Parse("#F6F7F8"));
        resources[ThemeInsetBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#1C1E22") : Color.Parse("#ECEEF0"));
        // Standby is easy to leave on. Amber fill plus wording, with dark text on the action button.
        resources[StandbyBannerBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#3A2A12") : Color.Parse("#FFF3D6"));
        resources[StandbyBannerForegroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#FFD27A") : Color.Parse("#7A4100"));
        resources[StandbyBannerBorderKey] = new SolidColorBrush(
            isDark ? Color.Parse("#E09A20") : Color.Parse("#C48412"));
        resources[StandbyActionBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#E09A20") : Color.Parse("#E89B1E"));
        resources[StandbyActionForegroundKey] = new SolidColorBrush(Color.Parse("#1A1A1A"));
        resources[StandbyActionHoverBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#F0B040") : Color.Parse("#D4890C"));
        resources[StandbyActionPressedBackgroundKey] = new SolidColorBrush(
            isDark ? Color.Parse("#C48412") : Color.Parse("#B8730A"));
        resources[SatelliteNoticeBackgroundKey] = resources[StandbyBannerBackgroundKey];
        resources[SatelliteNoticeForegroundKey] = resources[StandbyBannerForegroundKey];
        resources[SatelliteNoticeBorderKey] = resources[StandbyBannerBorderKey];
    }
}
