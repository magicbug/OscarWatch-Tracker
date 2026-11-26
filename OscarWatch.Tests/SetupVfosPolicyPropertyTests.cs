// Feature: test-coverage-expansion, Property 22: Unrecognised Mode Default Behaviour
// Feature: test-coverage-expansion, Property 23: Mode Case and Whitespace Insensitivity
// Feature: test-coverage-expansion, Property 24: IsLinearMode Complement

using FsCheck.Xunit;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 9.4, 9.5, 9.7**
///
/// Property-based tests verifying mode invariants of
/// <see cref="SetupVfosPolicy"/>: unrecognised mode default behaviour,
/// case/whitespace insensitivity for recognised modes, and IsLinearMode
/// complement property.
/// </summary>
public class SetupVfosPolicyPropertyTests
{
    private static readonly string[] RecognisedModes =
        { "FM", "FMN", "LSB", "USB", "CW", "DATA-LSB", "DATA-USB", "DATA-FM", "DV" };

    private static readonly string[] LinearModes = { "LSB", "USB", "CW" };

    /// <summary>
    /// Property 22: Unrecognised Mode Default Behaviour.
    ///
    /// For any string that does not match a recognised mode (case-insensitive,
    /// trimmed), Evaluate returns the linear threshold with Interactive=true.
    /// </summary>
    [Property]
    public bool Unrecognised_mode_returns_linear_threshold_with_interactive_true(
        string rawMode, int fmThreshold, int linearThreshold)
    {
        if (rawMode == null)
            return true; // skip null — Trim() would throw

        var trimmedUpper = rawMode.Trim().ToUpperInvariant();

        // Only test strings that are NOT recognised modes
        if (IsRecognisedMode(trimmedUpper))
            return true; // skip recognised modes

        var result = SetupVfosPolicy.Evaluate(rawMode, fmThreshold, linearThreshold);

        return result.ThresholdHz == linearThreshold && result.Interactive == true;
    }

    /// <summary>
    /// Property 23: Mode Case and Whitespace Insensitivity.
    ///
    /// For any recognised mode string, adding arbitrary leading/trailing
    /// whitespace or changing letter case does not change the result of Evaluate.
    /// </summary>
    [Property]
    public bool Case_and_whitespace_insensitivity_for_recognised_modes(
        int modeIndex, bool useUpper, int leadingSpaces, int trailingSpaces,
        int fmThreshold, int linearThreshold)
    {
        // Pick a recognised mode via modular index
        var baseMode = RecognisedModes[((modeIndex % RecognisedModes.Length) + RecognisedModes.Length) % RecognisedModes.Length];

        // Apply case transformation
        var transformedMode = useUpper ? baseMode.ToUpperInvariant() : baseMode.ToLowerInvariant();

        // Apply whitespace (constrain to reasonable amount)
        var leading = new string(' ', Math.Abs(leadingSpaces % 5));
        var trailing = new string(' ', Math.Abs(trailingSpaces % 5));
        var paddedMode = leading + transformedMode + trailing;

        var baseResult = SetupVfosPolicy.Evaluate(baseMode, fmThreshold, linearThreshold);
        var transformedResult = SetupVfosPolicy.Evaluate(paddedMode, fmThreshold, linearThreshold);

        return baseResult.ThresholdHz == transformedResult.ThresholdHz
            && baseResult.Interactive == transformedResult.Interactive;
    }

    /// <summary>
    /// Property 24: IsLinearMode Complement.
    ///
    /// For any string that is not "LSB", "USB", or "CW" (case-insensitive,
    /// trimmed), IsLinearMode returns false.
    /// </summary>
    [Property]
    public bool IsLinearMode_returns_false_for_non_linear_modes(string rawMode)
    {
        if (rawMode == null)
            return true; // skip null — Trim() would throw

        var trimmedUpper = rawMode.Trim().ToUpperInvariant();

        // Only test strings that are NOT linear modes
        if (IsLinearModeString(trimmedUpper))
            return true; // skip linear modes

        return SetupVfosPolicy.IsLinearMode(rawMode) == false;
    }

    private static bool IsRecognisedMode(string trimmedUpper)
    {
        return trimmedUpper is "FM" or "FMN" or "LSB" or "USB" or "CW" or "DATA-LSB" or "DATA-USB" or "DATA-FM" or "DV";
    }

    private static bool IsLinearModeString(string trimmedUpper)
    {
        return trimmedUpper is "LSB" or "USB" or "CW";
    }
}
