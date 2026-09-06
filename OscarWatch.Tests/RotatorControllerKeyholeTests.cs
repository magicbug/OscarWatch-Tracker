// Feature: smart-antenna-rotation, Property 7 + Unit Tests: RotatorController keyhole branches

using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Rotator;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

/// <summary>
/// Combined property-based and unit tests for RotatorController keyhole avoidance branches.
/// Property 7 validates the feature gate; unit tests cover pre-positioning, flipped tracking,
/// transitions, fallback, and normal-pass behaviour.
/// </summary>
public sealed class RotatorControllerKeyholeTests
{
    #region Helpers

    private static RotatorSettings EnabledSettings(
        bool keyholeEnabled = true,
        double keyholeThreshold = 80.0,
        double slewRate = 3.0,
        double parkAz = 0.0) => new()
    {
        Enabled = true,
        Port = "COM3",
        BaudRate = 9600,
        Type = RotatorType.YaesuGs232,
        TrackStartElevationDeg = 5,
        KeyholeAvoidanceEnabled = keyholeEnabled,
        KeyholeThresholdDeg = keyholeThreshold,
        SlewRateDegPerSec = slewRate,
        ParkAzimuthDeg = parkAz
    };

    private static SatelliteTrackState TrackTarget(
        string noradId,
        double azimuthDeg,
        double elevationDeg,
        double? aheadAzimuthDeg = null) => new()
    {
        Name = "TEST",
        NoradId = noradId,
        Subpoint = new GeoCoordinate(0, 0),
        LookAngles = new LookAngles(azimuthDeg, elevationDeg, 800, 0),
        AheadAzimuthDeg = aheadAzimuthDeg
    };

    private static PassInfo MakePass(
        double maxElevation,
        double aosAzimuth = 30.0,
        DateTime? aosUtc = null)
    {
        var aos = aosUtc ?? new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        return new PassInfo
        {
            SatelliteName = "TEST-SAT",
            NoradId = "99999",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(10),
            MaxElevationDeg = maxElevation,
            MaxElevationUtc = aos.AddMinutes(5),
            AosAzimuthDeg = aosAzimuth,
            LosAzimuthDeg = 200.0
        };
    }

    /// <summary>
    /// Creates a FlippedStart keyhole plan with the specified flipped start azimuth and lead time.
    /// </summary>
    private static KeyholePlan FlippedPlan(
        double flippedStartAz = 210.0,
        TimeSpan? leadTime = null) => new(
        Strategy: KeyholeStrategy.FlippedStart,
        FlippedStartAzimuthDeg: flippedStartAz,
        PrePositionLeadTime: leadTime ?? TimeSpan.FromSeconds(75),
        NormalSignalLossWindow: TimeSpan.FromSeconds(30),
        FlippedSignalLossWindow: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Creates a RotatorController with a recording driver and injects a flipped keyhole plan.
    /// Returns the controller and the recording driver for assertion.
    /// </summary>
    private static (RotatorController Controller, RecordingRotatorDriver Rotator)
        CreateControllerWithFlippedPlan(
            PassInfo pass,
            KeyholePlan? plan = null,
            RotatorSettings? settings = null)
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var s = settings ?? EnabledSettings();

        // First update to initialise state (sets cached settings and target)
        controller.UpdateSynchronously(s, TrackTarget("99999", 30, 2));

        // Set the active pass
        controller.SetActivePassSynchronously(pass);

        // Inject the plan directly for testing
        controller.SetKeyholePlanForTests(plan ?? FlippedPlan());

        return (controller, rotator);
    }

    #endregion

    #region Property 7: Feature Gate — Disabled Means Normal Tracking

    /// <summary>
    /// Feature: smart-antenna-rotation, Property 7: Feature Gate — Disabled Means Normal Tracking
    ///
    /// For any pass and settings with KeyholeAvoidanceEnabled = false, the controller uses
    /// normal tracking without invoking keyhole planning (no flipped azimuth, no pre-positioning).
    ///
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FeatureDisabled_MeansNormalTracking_NoKeyholeActive(
        byte azByte,
        byte elByte,
        byte maxElevByte)
    {
        // Generate a target at random azimuth and elevation above tracking threshold
        var az = azByte / 255.0 * 359.9;         // [0, 359.9]
        var el = 5.0 + (elByte / 255.0 * 85.0);  // [5, 90] — above TrackStartElevationDeg
        var maxElev = 60.0 + (maxElevByte / 255.0 * 30.0); // [60, 90] — could be a keyhole pass

        var rotator = new RecordingRotatorDriver();
        var settings = EnabledSettings(keyholeEnabled: false);

        // Create a pass that would normally trigger keyhole avoidance
        var pass = MakePass(maxElev, az);

        var controller = new RotatorController(_ => rotator);

        // Set up the active pass and update with a target
        controller.SetActivePassSynchronously(pass);
        controller.UpdateSynchronously(settings, TrackTarget("99999", az, el));

        var status = controller.GetPositionStatus();

        // With feature disabled: no keyhole avoidance, no pre-positioning
        return !status.IsKeyholeAvoidanceActive && !status.IsPrePositioning;
    }

    [Fact]
    public void Keyhole_avoidance_requires_0_180_elevation_range()
    {
        var rotator = new RecordingRotatorDriver();
        var settings = EnabledSettings(keyholeEnabled: true);
        settings.ElevationRange = RotatorElevationRange.Deg90;

        var controller = new RotatorController(_ => rotator);
        controller.SetActivePassSynchronously(MakePass(maxElevation: 85));
        controller.UpdateSynchronously(settings, TrackTarget("99999", 45, 60));

        Assert.Null(controller.CurrentKeyholePlan);
        Assert.False(controller.GetPositionStatus().IsKeyholeAvoidanceActive);
    }

    #endregion

    #region Unit Tests — Pre-Positioning

    /// <summary>
    /// When a flipped plan is active and the current time is in the pre-position window
    /// (AOS − PrePositionLeadTime ≤ now &lt; AOS), the controller commands the rotator to
    /// slew to the flipped start azimuth at the track-start elevation.
    ///
    /// Requirements: 3.2
    /// </summary>
    [Fact]
    public void PrePositioning_CommandsFlippedStartAzimuth_AtCorrectTime()
    {
        var rotator = new RecordingRotatorDriver();
        // AOS 30 seconds from now, within pre-position window (lead time = 75s)
        var aosUtc = DateTime.UtcNow.AddSeconds(30);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0, leadTime: TimeSpan.FromSeconds(75));

        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        // Initial update to set target and connect
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        // Set the active pass and inject the flipped plan
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);

        // Now update again: the pre-positioning branch should activate
        // Target elevation is below tracking threshold, so tracking won't override
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        // The controller should be pre-positioning
        Assert.True(controller.IsPrePositioning);

        // Flipped start azimuth (210°) at track-start elevation (5° in EnabledSettings)
        Assert.Equal(210, rotator.LastAzimuthDeg);
        Assert.Equal(5, rotator.LastElevationDeg);

        var status = controller.GetPositionStatus();
        Assert.True(status.IsPrePositioning);
    }

    [Fact]
    public void PrePositioning_UsesTrackStartElevation()
    {
        var rotator = new RecordingRotatorDriver();
        var aosUtc = DateTime.UtcNow.AddSeconds(30);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0, leadTime: TimeSpan.FromSeconds(75));
        var settings = EnabledSettings();
        settings.TrackStartElevationDeg = 25;

        var controller = new RotatorController(_ => rotator);
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        Assert.True(controller.IsPrePositioning);
        Assert.Equal(210, rotator.LastAzimuthDeg);
        Assert.Equal(25, rotator.LastElevationDeg);
    }

    [Fact]
    public void PrePositioning_ClampsNegativeTrackStartToHorizon()
    {
        var rotator = new RecordingRotatorDriver();
        var aosUtc = DateTime.UtcNow.AddSeconds(30);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0, leadTime: TimeSpan.FromSeconds(75));
        var settings = EnabledSettings();
        settings.TrackStartElevationDeg = -3;

        var controller = new RotatorController(_ => rotator);
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        Assert.True(controller.IsPrePositioning);
        Assert.Equal(210, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
        Assert.Equal(0, RotatorController.KeyholePrePositionElevationDeg(settings));
    }

    /// <summary>
    /// When there is insufficient lead time for pre-positioning (remaining time before AOS
    /// is less than 1 second), the controller falls back to normal tracking.
    ///
    /// Requirements: 3.4
    /// </summary>
    [Fact]
    public void PrePositioning_FallsBackToNormal_WhenInsufficientLeadTime()
    {
        var rotator = new RecordingRotatorDriver();
        // AOS is essentially now (within 500ms) — insufficient lead time
        var aosUtc = DateTime.UtcNow.AddMilliseconds(500);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0, leadTime: TimeSpan.FromSeconds(75));

        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        // Initial update
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        // Set pass and inject plan
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);

        // Update — should fall back because of insufficient lead time
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 2));

        // Should NOT be pre-positioning
        Assert.False(controller.IsPrePositioning);
        var status = controller.GetPositionStatus();
        Assert.False(status.IsPrePositioning);
    }

    #endregion

    #region Unit Tests — Flipped Tracking

    /// <summary>
    /// When a FlippedStart plan is active and satellite elevation is at or above the keyhole
    /// threshold, the commanded azimuth is (compass + 180) % 360.
    ///
    /// Requirements: 4.1
    /// </summary>
    [Fact]
    public void FlippedTracking_CommandsAzimuthPlus180()
    {
        var rotator = new RecordingRotatorDriver();
        // AOS in the past so pre-positioning window is closed
        var aosUtc = DateTime.UtcNow.AddMinutes(-5);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0);

        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings(keyholeThreshold: 80.0);

        // Initial update to connect and set up state
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 10));

        // Set pass and inject plan
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);

        // Track target at 45° azimuth, 85° elevation (above threshold of 80°)
        controller.UpdateSynchronously(settings, TrackTarget("99999", 45, 85));

        // Should be in flipped mode
        Assert.True(controller.IsKeyholeFlippedActive);

        // Commanded azimuth should be (45 + 180) % 360 = 225
        Assert.Equal(225, rotator.LastAzimuthDeg);
        Assert.Equal(85, rotator.LastElevationDeg);

        var status = controller.GetPositionStatus();
        Assert.True(status.IsKeyholeAvoidanceActive);
    }

    /// <summary>
    /// When the satellite elevation drops below the keyhole threshold during a flipped pass,
    /// the controller transitions back to normal compass azimuth tracking.
    ///
    /// Requirements: 4.2
    /// </summary>
    [Fact]
    public void FlippedTracking_TransitionsToNormal_WhenElevationDropsBelowThreshold()
    {
        var rotator = new RecordingRotatorDriver();
        var aosUtc = DateTime.UtcNow.AddMinutes(-5);
        var pass = MakePass(88.0, 30.0, aosUtc);
        var plan = FlippedPlan(flippedStartAz: 210.0);

        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings(keyholeThreshold: 80.0);

        // Initial update to connect
        controller.UpdateSynchronously(settings, TrackTarget("99999", 30, 10));

        // Set pass and inject plan
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(plan);

        // First: track at high elevation (above threshold) to enter flipped mode
        controller.UpdateSynchronously(settings, TrackTarget("99999", 45, 85));
        Assert.True(controller.IsKeyholeFlippedActive);
        Assert.Equal(225, rotator.LastAzimuthDeg); // flipped: (45+180)%360

        // Now: elevation drops below threshold — should transition to normal
        controller.UpdateSynchronously(settings, TrackTarget("99999", 200, 70));
        Assert.False(controller.IsKeyholeFlippedActive);
        Assert.Equal(200, rotator.LastAzimuthDeg); // normal compass azimuth

        var status = controller.GetPositionStatus();
        Assert.False(status.IsKeyholeAvoidanceActive);
    }

    #endregion

    #region Unit Tests — Normal Pass with Feature Enabled

    /// <summary>
    /// When the feature is enabled but the pass is classified as normal (planner recommends
    /// Normal), the controller tracks using compass azimuth without flipping.
    ///
    /// Requirements: 5.1, 5.3
    /// </summary>
    [Fact]
    public void NormalPass_TrackedNormally_WhenFeatureEnabled()
    {
        var rotator = new RecordingRotatorDriver();
        var aosUtc = DateTime.UtcNow.AddMinutes(-2);
        var pass = MakePass(45.0, 100.0, aosUtc);

        // Normal plan (planner recommends Normal strategy)
        var normalPlan = new KeyholePlan(
            Strategy: KeyholeStrategy.Normal,
            FlippedStartAzimuthDeg: null,
            PrePositionLeadTime: null,
            NormalSignalLossWindow: TimeSpan.Zero,
            FlippedSignalLossWindow: TimeSpan.Zero);

        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings(keyholeEnabled: true, keyholeThreshold: 80.0);

        // Initial update to connect
        controller.UpdateSynchronously(settings, TrackTarget("99999", 100, 10));

        // Set pass and inject Normal plan
        controller.SetActivePassSynchronously(pass);
        controller.SetKeyholePlanForTests(normalPlan);

        // Track at various positions — should use compass azimuth directly
        controller.UpdateSynchronously(settings, TrackTarget("99999", 120, 30));
        Assert.Equal(120, rotator.LastAzimuthDeg);
        Assert.False(controller.IsKeyholeFlippedActive);
        Assert.False(controller.IsPrePositioning);

        var status = controller.GetPositionStatus();
        Assert.False(status.IsKeyholeAvoidanceActive);
        Assert.False(status.IsPrePositioning);
    }

    #endregion
}
