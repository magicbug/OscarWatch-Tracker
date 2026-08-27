using OscarWatch.Core.Radio;
using System.Reflection;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for the FlexSmartSdrCodec.ParseKeyValues Span optimization.
/// Verifies functional equivalence and behavioral correctness.
/// </summary>
public class FlexSmartSdrCodecParseKeyValuesOptimizationTests
{
    [Fact]
    public void ParseKeyValues_handles_simple_key_value_pairs()
    {
        // Arrange
        var input = "freq=14.230 mode=USB active=1";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("14.230", result["freq"]);
        Assert.Equal("USB", result["mode"]);  
        Assert.Equal("1", result["active"]);
    }

    [Fact]
    public void ParseKeyValues_handles_mixed_whitespace_delimiters()
    {
        // Arrange
        var input = "freq=14.230\tmode=USB\r\nactive=1 tx=0";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("14.230", result["freq"]);
        Assert.Equal("USB", result["mode"]);
        Assert.Equal("1", result["active"]);
        Assert.Equal("0", result["tx"]);
    }

    [Fact]
    public void ParseKeyValues_ignores_malformed_tokens()
    {
        // Arrange - Mix valid and invalid tokens
        var input = "freq=14.230 invalid mode=USB =badkey goodkey= active=1";

        // Act
        var result = CallParseKeyValues(input);

        // Assert - Should only include valid key=value pairs
        Assert.Equal(3, result.Count);
        Assert.Equal("14.230", result["freq"]);
        Assert.Equal("USB", result["mode"]);
        Assert.Equal("1", result["active"]);
        Assert.False(result.ContainsKey("invalid"));
        Assert.False(result.ContainsKey("badkey"));
        Assert.False(result.ContainsKey("goodkey"));
    }

    [Fact]
    public void ParseKeyValues_handles_case_insensitive_keys()
    {
        // Arrange
        var input = "FREQ=14.230 Mode=USB active=1";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("14.230", result["freq"]); // Should be case insensitive
        Assert.Equal("USB", result["MODE"]);    // Should be case insensitive  
        Assert.Equal("1", result["ACTIVE"]);    // Should be case insensitive
    }

    [Fact]
    public void ParseKeyValues_handles_empty_and_whitespace_only_input()
    {
        // Arrange & Act & Assert
        Assert.Empty(CallParseKeyValues(""));
        Assert.Empty(CallParseKeyValues(" "));
        Assert.Empty(CallParseKeyValues("\t\r\n"));
        Assert.Empty(CallParseKeyValues("   \t  \r\n  "));
    }

    [Fact]
    public void ParseKeyValues_handles_values_with_special_characters()
    {
        // Arrange
        var input = "pan=0x40000000 mode=AM txant=ANT1 call=W1AW/B";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("0x40000000", result["pan"]);
        Assert.Equal("AM", result["mode"]);
        Assert.Equal("ANT1", result["txant"]);
        Assert.Equal("W1AW/B", result["call"]);
    }

    [Fact]
    public void ParseKeyValues_handles_numeric_values()
    {
        // Arrange
        var input = "freq=14.230123 active=1 tx=0 rf_power=50.5";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("14.230123", result["freq"]);
        Assert.Equal("1", result["active"]);
        Assert.Equal("0", result["tx"]);
        Assert.Equal("50.5", result["rf_power"]);
    }

    [Fact]
    public void ParseKeyValues_handles_realistic_flex_status_message()
    {
        // Arrange - Based on actual Flex radio status messages
        var input = "in_use=1 RF_frequency=14.230 mode=USB active=1 tx=0 wide=0 " +
                   "pan=0x40000000 txant=ANT1 rxant=ANT1 fm_tone_mode=off";

        // Act
        var result = CallParseKeyValues(input);

        // Assert - There are 10 key-value pairs in the input string
        Assert.Equal(10, result.Count);
        Assert.Equal("1", result["in_use"]);
        Assert.Equal("14.230", result["RF_frequency"]);
        Assert.Equal("USB", result["mode"]);
        Assert.Equal("1", result["active"]);
        Assert.Equal("0", result["tx"]);
        Assert.Equal("0", result["wide"]);
        Assert.Equal("0x40000000", result["pan"]);
        Assert.Equal("ANT1", result["txant"]);
        Assert.Equal("ANT1", result["rxant"]);
        Assert.Equal("off", result["fm_tone_mode"]);
    }

    [Fact]
    public void ParseKeyValues_handles_leading_and_trailing_whitespace()
    {
        // Arrange
        var input = "  freq=14.230   mode=USB   active=1  ";

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("14.230", result["freq"]);
        Assert.Equal("USB", result["mode"]);
        Assert.Equal("1", result["active"]);
    }

    [Fact]
    public void ParseKeyValues_handles_large_input_efficiently()
    {
        // Arrange - Create a larger input to test allocation efficiency
        var tokens = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            tokens.Add($"key{i}=value{i}");
        }
        var input = string.Join(" ", tokens);

        // Act
        var result = CallParseKeyValues(input);

        // Assert
        Assert.Equal(100, result.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal($"value{i}", result[$"key{i}"]);
        }
    }

    /// <summary>
    /// Uses reflection to call the private ParseKeyValues method.
    /// This ensures we're testing the actual optimized implementation.
    /// </summary>
    private static Dictionary<string, string> CallParseKeyValues(string input)
    {
        var type = typeof(FlexSmartSdrCodec);
        var method = type.GetMethod("ParseKeyValues", BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, [input]);
        return Assert.IsType<Dictionary<string, string>>(result);
    }
}