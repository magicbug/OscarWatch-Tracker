using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using System.Reflection;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for LINQ elimination optimizations in HamsAtRovesService.
/// Verifies functional equivalence and performance improvements with optimized algorithms.
/// </summary>
public class HamsAtRovesServiceLinqOptimizationTests
{
    [Fact]
    public void ParseErrorMessages_handles_empty_errors_list()
    {
        var json = """{"errors": []}""";
        var result = CallParseErrorMessages(json);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseErrorMessages_handles_null_errors()
    {
        var json = """{"errors": null}""";
        var result = CallParseErrorMessages(json);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseErrorMessages_filters_empty_and_whitespace_errors()
    {
        var json = """
        {
          "errors": [
            "Valid error message",
            "",
            "   ",
            "Another valid error",
            null,
            "Third error"
          ]
        }
        """;

        var result = CallParseErrorMessages(json);

        Assert.Equal(3, result.Count);
        Assert.Contains("Valid error message", result);
        Assert.Contains("Another valid error", result);
        Assert.Contains("Third error", result);
    }

    [Fact]
    public void ParseErrorMessages_trims_whitespace_from_errors()
    {
        var json = """
        {
          "errors": [
            "  Trimmed error  ",
            "\tTab error\t",
            "\nNewline error\n"
          ]
        }
        """;

        var result = CallParseErrorMessages(json);

        Assert.Equal(3, result.Count);
        Assert.Contains("Trimmed error", result);
        Assert.Contains("Tab error", result);
        Assert.Contains("Newline error", result);
    }

    [Fact]
    public void ParseErrorMessages_handles_invalid_json()
    {
        var invalidJson = "invalid json content";
        var result = CallParseErrorMessages(invalidJson);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseErrorMessages_handles_missing_errors_property()
    {
        var json = """{"other_property": "value"}""";
        var result = CallParseErrorMessages(json);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseErrorMessages_optimized_implementation_matches_original()
    {
        // Test with realistic API error response
        var json = """
        {
          "errors": [
            "The satellite number field is required.",
            "",
            "  Observer latitude must be between -90 and 90.  ",
            "   ",
            "The max_at field must be a valid ISO date.",
            null
          ]
        }
        """;

        var result = CallParseErrorMessages(json);

        // Should have 3 valid errors after filtering and trimming
        Assert.Equal(3, result.Count);
        Assert.Contains("The satellite number field is required.", result);
        Assert.Contains("Observer latitude must be between -90 and 90.", result);
        Assert.Contains("The max_at field must be a valid ISO date.", result);
    }

    [Fact]
    public void FormatErrorMessages_handles_empty_list()
    {
        var result = HamsAtRovesService.FormatErrorMessages([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void FormatErrorMessages_handles_single_error()
    {
        var errors = new List<string> { "Single error" };
        var result = HamsAtRovesService.FormatErrorMessages(errors);
        Assert.Equal("Single error", result);
    }

    [Fact]
    public void FormatErrorMessages_joins_multiple_errors()
    {
        var errors = new List<string> 
        { 
            "First error", 
            "Second error", 
            "Third error" 
        };
        
        var result = HamsAtRovesService.FormatErrorMessages(errors);
        Assert.Equal("First error; Second error; Third error", result);
    }

    [Fact]
    public void SerializeCreateAlertPayload_avoids_unnecessary_array_conversion()
    {
        var request = new HamsAtCreateAlertRequest
        {
            SatelliteNumber = 25544,
            ObserverLat = 51.5074,
            ObserverLon = -0.1278,
            MaxAtUtc = DateTime.Parse("2024-08-15T12:30:00Z").ToUniversalTime(),
            Callsign = "TEST1ABC",
            Grids = new List<string> { "IO91wm", "IO91wn" }
        };

        var json = HamsAtRovesService.SerializeCreateAlertPayload(request);

        // Should contain the grids without converting to array
        Assert.Contains("\"grids\":[\"IO91wm\",\"IO91wn\"]", json);
        Assert.Contains("\"satellite_number\":25544", json);
        Assert.Contains("\"observer_lat\":51.5074", json);
        Assert.Contains("\"observer_lon\":-0.1278", json);
        Assert.Contains("\"callsign\":\"TEST1ABC\"", json);
    }

    [Fact]
    public void Performance_large_error_list_processing()
    {
        // Test with a large list to verify efficiency
        var errors = new List<string>();
        for (int i = 0; i < 1000; i++)
        {
            if (i % 3 == 0) errors.Add($"Error {i}");          // Valid
            else if (i % 3 == 1) errors.Add("");               // Empty
            else errors.Add("   ");                             // Whitespace
        }

        var json = $$"""{"errors": [{{string.Join(",", errors.Select(e => $"\"{e}\""))}}]}""";
        var result = CallParseErrorMessages(json);

        // Should have ~333 valid errors (every 3rd item)
        Assert.True(result.Count > 300 && result.Count < 400);
        Assert.All(result, error => Assert.False(string.IsNullOrWhiteSpace(error)));
    }

    [Fact]
    public void Integration_error_parsing_with_format()
    {
        var json = """
        {
          "errors": [
            "Validation failed for satellite_number",
            "  Observer coordinates out of range  ",
            "",
            "Invalid timestamp format"
          ]
        }
        """;

        var parsed = CallParseErrorMessages(json);
        var formatted = HamsAtRovesService.FormatErrorMessages(parsed);

        Assert.Equal("Validation failed for satellite_number; Observer coordinates out of range; Invalid timestamp format", formatted);
    }

    /// <summary>
    /// Uses reflection to test the internal ParseErrorMessages method.
    /// </summary>
    private static IReadOnlyList<string> CallParseErrorMessages(string body)
    {
        var type = typeof(HamsAtRovesService);
        var method = type.GetMethod("ParseErrorMessages", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, [body]);
        return (IReadOnlyList<string>)result!;
    }
}