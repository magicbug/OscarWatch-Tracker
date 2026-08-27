using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for string processing optimizations in SatelliteDatabaseService.
/// Verifies functional equivalence and behavioral correctness of Span-based string operations.
/// </summary>
public class SatelliteDatabaseServiceStringOptimizationTests
{
    [Fact]
    public void NormalizeName_handles_spaces_and_dashes()
    {
        // Use reflection to test the private method
        var result = CallNormalizeName("AO-7 OSCAR");
        Assert.Equal("AO7OSCAR", result);
    }

    [Fact]
    public void NormalizeName_handles_case_conversion()
    {
        var result = CallNormalizeName("ao-7 oscar");
        Assert.Equal("AO7OSCAR", result);
    }

    [Fact]
    public void NormalizeName_handles_mixed_case()
    {
        var result = CallNormalizeName("AO-7 oscar");
        Assert.Equal("AO7OSCAR", result);
    }

    [Fact]
    public void NormalizeName_handles_multiple_spaces_and_dashes()
    {
        var result = CallNormalizeName("FOX - 1B  RADFXSAT");
        Assert.Equal("FOX1BRADFXSAT", result);
    }

    [Fact]
    public void NormalizeName_handles_empty_and_null()
    {
        Assert.Equal("", CallNormalizeName(""));
        Assert.Equal("", CallNormalizeName("   "));
        Assert.Equal("", CallNormalizeName(null!));
    }

    [Fact]
    public void NormalizeName_handles_no_normalization_needed()
    {
        var result = CallNormalizeName("ISS");
        Assert.Equal("ISS", result);
    }

    [Fact]
    public void NormalizeName_handles_only_spaces()
    {
        var result = CallNormalizeName("ZARYA MODULES");
        Assert.Equal("ZARYAMODULES", result);
    }

    [Fact]
    public void NormalizeName_handles_only_dashes()
    {
        var result = CallNormalizeName("AO-7-LEGACY");
        Assert.Equal("AO7LEGACY", result);
    }

    [Fact]
    public void NormalizeName_handles_special_characters_preservation()
    {
        var result = CallNormalizeName("SO-50 (SAUDISAT)");
        Assert.Equal("SO50(SAUDISAT)", result);
    }

    [Fact]
    public void NormalizeName_handles_large_input()
    {
        var longName = "AO-7 OSCAR AMSAT   DIGITAL   REPEATER  SATELLITE  MODE  B  LINEAR  TRANSPONDER";
        var result = CallNormalizeName(longName);
        Assert.Equal("AO7OSCARAMSATDIGITALREPEATERSATELLITEMODEBLINEARTRANSPONDER", result);
    }

    [Fact]
    public void Database_lookup_with_normalized_names()
    {
        // Integration test with actual database service
        var tempFile = Path.GetTempFileName();
        try
        {
            // Create test database file
            var testData = new[]
            {
                new SatelliteRadioEntry
                {
                    Name = "AO-7",
                    NoradId = "07530",
                    AlternativeNames = new List<string> { "OSCAR 7", "AO 7" },
                    Modes = new List<SatelliteTransponderMode>
                    {
                        new SatelliteTransponderMode
                        {
                            Type = "Linear",
                            DownlinkKHz = 29400,
                            UplinkKHz = 145850,
                            DownlinkMode = "CW",
                            UplinkMode = "CW",
                            Doppler = "NOR"
                        }
                    }
                }
            };
            
            SatelliteDatabaseFile.Save(tempFile, testData.ToList());
            var service = new SatelliteDatabaseService(tempFile, tempFile);

            // Test various lookup patterns that should resolve to the same entry
            var byMainName = service.TryGetEntry("AO-7");
            var byAlias1 = service.TryGetEntry("OSCAR 7");
            var byAlias2 = service.TryGetEntry("AO 7");
            var byNormalized = service.TryGetEntry("ao7"); // Should work via normalization

            Assert.NotNull(byMainName);
            Assert.NotNull(byAlias1);  
            Assert.NotNull(byAlias2);
            Assert.NotNull(byNormalized);
            Assert.Equal("AO-7", byMainName.Name);
            Assert.Equal(byMainName.NoradId, byAlias1.NoradId);
            Assert.Equal(byMainName.NoradId, byAlias2.NoradId);
            Assert.Equal(byMainName.NoradId, byNormalized.NoradId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Parenthetical_alias_optimization()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var testData = new[]
            {
                new SatelliteRadioEntry
                {
                    Name = "RADFXSAT (FOX-1B)",
                    NoradId = "43017",
                    Modes = new List<SatelliteTransponderMode>
                    {
                        new SatelliteTransponderMode
                        {
                            Type = "FM",
                            DownlinkKHz = 145980,
                            UplinkKHz = 435350,
                            DownlinkMode = "FM",
                            UplinkMode = "FM",
                            Doppler = "NOR"
                        }
                    }
                }
            };

            SatelliteDatabaseFile.Save(tempFile, testData.ToList());
            var service = new SatelliteDatabaseService(tempFile, tempFile);

            // Should find by full name
            var byFull = service.TryGetEntry("RADFXSAT (FOX-1B)");
            // Should find by parenthetical prefix
            var byPrefix = service.TryGetEntry("RADFXSAT");

            Assert.NotNull(byFull);
            Assert.NotNull(byPrefix);
            Assert.Equal(byFull.NoradId, byPrefix.NoradId);
            Assert.Equal("RADFXSAT (FOX-1B)", byFull.Name);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Uses reflection to test the private NormalizeName method.
    /// </summary>
    private static string CallNormalizeName(string input)
    {
        var type = typeof(SatelliteDatabaseService);
        var method = type.GetMethod("NormalizeName", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, [input]);
        return Assert.IsType<string>(result);
    }
}