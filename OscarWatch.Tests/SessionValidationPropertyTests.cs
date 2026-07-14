using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for session validation logic.
/// Validates correctness properties 13–14 from the session-planner design document.
/// </summary>
public sealed class SessionValidationPropertyTests
{
    // ─── Property 13: 15-Minute Rounding ─────────────────────────────────────────
    // Feature: session-planner, Property 13: 15-Minute Rounding
    // **Validates: Requirements 1.5**

    /// <summary>
    /// For any DateTime value, RoundUpTo15Minutes SHALL produce a result where
    /// (a) minutes are divisible by 15 and seconds are zero,
    /// (b) the result is >= the input, and
    /// (c) the result is at most 15 minutes after the input.
    /// </summary>
    [Property]
    public bool FifteenMinuteRounding(long ticks)
    {
        // Constrain ticks to a valid DateTime range (year 2020-2030)
        var minTicks = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var maxTicks = new DateTime(2030, 12, 31, 23, 59, 59, DateTimeKind.Utc).Ticks;
        var range = maxTicks - minTicks;
        var constrainedTicks = minTicks + (((ticks % range) + range) % range);

        var input = new DateTime(constrainedTicks, DateTimeKind.Utc);
        var result = SessionPlannerService.RoundUpTo15Minutes(input);

        // (a) Minutes divisible by 15 and seconds are zero
        if (result.Minute % 15 != 0)
            return false;
        if (result.Second != 0)
            return false;

        // (b) Result >= input
        if (result < input)
            return false;

        // (c) Result is at most 15 minutes after input
        var diff = result - input;
        if (diff > TimeSpan.FromMinutes(15))
            return false;

        return true;
    }

    // ─── Property 14: Session Validation Rejects Invalid Windows ──────────────────
    // Feature: session-planner, Property 14: Session Validation Rejects Invalid Windows
    // **Validates: Requirements 1.3, 1.4**

    /// <summary>
    /// For any pair of DateTimes where end &lt;= start, calling GeneratePlanAsync
    /// SHALL throw ArgumentException. Test by constructing a service with null
    /// dependencies — validation runs before any service access.
    /// </summary>
    [Property]
    public bool ValidationRejectsEndBeforeOrEqualStart(long startTicks, int offsetMinutes)
    {
        // Constrain start to valid range
        var minTicks = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var maxTicks = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var range = maxTicks - minTicks;
        var constrainedTicks = minTicks + (((startTicks % range) + range) % range);

        var start = new DateTime(constrainedTicks, DateTimeKind.Utc);

        // Make end <= start: subtract a non-negative offset (0 means equal)
        var absOffset = ((offsetMinutes % 1440) + 1440) % 1440; // 0..1439 minutes
        var end = start.AddMinutes(-absOffset);

        // end <= start should cause ArgumentException
        try
        {
            // Create service with null dependencies — validation fires before any access
            var service = new SessionPlannerService(null!, null!, null!);
            service.GeneratePlanAsync(start, end).GetAwaiter().GetResult();
            return false; // Should have thrown
        }
        catch (ArgumentException)
        {
            return true; // Expected
        }
        catch
        {
            return false; // Unexpected exception type
        }
    }

    /// <summary>
    /// For any pair of DateTimes where the duration exceeds 48 hours, calling
    /// GeneratePlanAsync SHALL throw ArgumentException.
    /// </summary>
    [Property]
    public bool ValidationRejectsDurationOver48Hours(long startTicks, int extraHours)
    {
        // Constrain start to valid range
        var minTicks = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var maxTicks = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var range = maxTicks - minTicks;
        var constrainedTicks = minTicks + (((startTicks % range) + range) % range);

        var start = new DateTime(constrainedTicks, DateTimeKind.Utc);

        // Duration > 48 hours: add 48 hours plus at least 1 extra minute
        var extraMinutes = 1 + (((extraHours % 2880) + 2880) % 2880); // 1..2880 extra minutes
        var end = start.AddHours(48).AddMinutes(extraMinutes);

        try
        {
            var service = new SessionPlannerService(null!, null!, null!);
            service.GeneratePlanAsync(start, end).GetAwaiter().GetResult();
            return false; // Should have thrown
        }
        catch (ArgumentException)
        {
            return true; // Expected
        }
        catch
        {
            return false; // Unexpected exception type
        }
    }
}
