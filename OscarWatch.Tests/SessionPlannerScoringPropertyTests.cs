using FsCheck.Xunit;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 2.1, 2.2, 2.5**
///
/// Property-based tests verifying that <see cref="PassQualityScorer.ComputeScore"/>
/// always returns a value in the closed interval [0.0, 1.0], regardless of input values.
/// </summary>
public sealed class SessionPlannerScoringPropertyTests
{
    // Feature: session-planner, Property 1: Score Bounded in [0, 1]
    // **Validates: Requirements 2.1, 2.2, 2.5**

    /// <summary>
    /// For any maxElevationDeg (any double), durationMinutes (any double), and
    /// TransponderCategory value, ComputeScore SHALL return a value in [0.0, 1.0].
    /// Even for extreme inputs (negative elevation, very large duration, etc.)
    /// the score must stay within bounds due to clamping.
    /// </summary>
    [Property]
    public bool ScoreIsBoundedBetweenZeroAndOne(double elevation, double duration, int categorySeed)
    {
        var categories = (TransponderCategory[])typeof(TransponderCategory).GetEnumValues();
        var category = categories[((categorySeed % categories.Length) + categories.Length) % categories.Length];

        var score = PassQualityScorer.ComputeScore(elevation, duration, category);

        return score >= 0.0 && score <= 1.0;
    }

    // Feature: session-planner, Property 2: Composite Score Formula
    // **Validates: Requirements 3.4**

    /// <summary>
    /// For any quality score q in [0.0, 1.0] and satellite priority p in [1, 10],
    /// the composite score SHALL equal q × (11 − p), yielding a value in [0.0, 10.0].
    /// </summary>
    [Property]
    public bool CompositeScoreFollowsFormula(double rawQ, int rawP)
    {
        // Constrain q to [0, 1] by normalising via modular arithmetic on the absolute value
        var q = Math.Abs(rawQ % 1.0);
        if (double.IsNaN(q) || double.IsInfinity(q))
            q = 0.5;

        // Constrain p to [1, 10]
        var p = (((rawP % 10) + 10) % 10) + 1; // yields 1..10

        // Call the production formula
        var composite = PassQualityScorer.ComputeCompositeScore(q, p);

        // Verify the formula: composite = q * (11 - p)
        var expected = q * (11 - p);
        var formulaCorrect = Math.Abs(composite - expected) < 1e-10;

        // Verify the result is within [0.0, 10.0]
        var inRange = composite >= 0.0 && composite <= 10.0;

        return formulaCorrect && inRange;
    }
}
