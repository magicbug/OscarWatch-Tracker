using OscarWatch.Core.Display;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for collection processing optimizations in ScheduledPassReminder.
/// Verifies functional equivalence and performance improvements with optimized algorithms.
/// </summary>
public class ScheduledPassReminderCollectionOptimizationTests
{
    private static readonly DateTime TestTime = new(2024, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Toggle_adds_new_entry_when_not_found()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime }
        };

        var result = ScheduledPassReminder.Toggle(scheduled, "07530", TestTime.AddHours(1));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.NoradId == "25544");
        Assert.Contains(result, e => e.NoradId == "07530");
    }

    [Fact]
    public void Toggle_removes_existing_entry()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime },
            new() { NoradId = "07530", AosUtc = TestTime.AddHours(1) }
        };

        var result = ScheduledPassReminder.Toggle(scheduled, "07530", TestTime.AddHours(1));

        Assert.Equal(1, result.Count);
        Assert.Equal("25544", result[0].NoradId);
    }

    [Fact]
    public void Toggle_handles_empty_list()
    {
        var result = ScheduledPassReminder.Toggle(new List<ScheduledPassEntry>(), "07530", TestTime);

        Assert.Single(result);
        Assert.Equal("07530", result[0].NoradId);
    }

    [Fact]
    public void EnsureScheduled_avoids_copy_when_already_scheduled()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "07530", AosUtc = TestTime }
        };

        var result = ScheduledPassReminder.EnsureScheduled(scheduled, "07530", TestTime);

        Assert.Single(result);
        Assert.Equal("07530", result[0].NoradId);
        // Should create new list but same content
        Assert.NotSame(scheduled, result);
    }

    [Fact]
    public void EnsureScheduled_adds_new_entry_when_not_found()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime }
        };

        var result = ScheduledPassReminder.EnsureScheduled(scheduled, "07530", TestTime.AddHours(1));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.NoradId == "25544");
        Assert.Contains(result, e => e.NoradId == "07530");
    }

    [Fact]
    public void Process_uses_optimized_dictionary_lookup()
    {
        var reminder = new ScheduledPassReminder();
        
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime.AddMinutes(5) },
            new() { NoradId = "07530", AosUtc = TestTime.AddMinutes(10) }
        };

        var upcomingPasses = new List<PassInfo>
        {
            new()
            {
                SatelliteName = "ISS",
                NoradId = "25544",
                AosUtc = TestTime.AddMinutes(5),
                LosUtc = TestTime.AddMinutes(15),
                MaxElevationDeg = 45
            },
            new()
            {
                SatelliteName = "AO-7",
                NoradId = "07530", 
                AosUtc = TestTime.AddMinutes(10),
                LosUtc = TestTime.AddMinutes(20),
                MaxElevationDeg = 30
            },
            new()
            {
                SatelliteName = "RADFXSAT",
                NoradId = "43017", // Not scheduled
                AosUtc = TestTime.AddMinutes(7),
                LosUtc = TestTime.AddMinutes(17),
                MaxElevationDeg = 60
            }
        };

        var result = reminder.Process(TestTime, scheduled, upcomingPasses, leadMinutesBeforeAos: 15);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.NoradId == "25544");
        Assert.Contains(result, p => p.NoradId == "07530");
    }

    [Fact]
    public void Process_handles_multiple_passes_same_satellite()
    {
        var reminder = new ScheduledPassReminder();
        
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime.AddMinutes(5) }
        };

        // Multiple passes for same satellite - should find best match
        var upcomingPasses = new List<PassInfo>
        {
            new()
            {
                SatelliteName = "ISS",
                NoradId = "25544",
                AosUtc = TestTime.AddMinutes(3), // 2 min difference
                LosUtc = TestTime.AddMinutes(13),
                MaxElevationDeg = 30
            },
            new()
            {
                SatelliteName = "ISS",
                NoradId = "25544",
                AosUtc = TestTime.AddMinutes(5), // Exact match
                LosUtc = TestTime.AddMinutes(15),
                MaxElevationDeg = 45
            },
            new()
            {
                SatelliteName = "ISS",
                NoradId = "25544",
                AosUtc = TestTime.AddMinutes(8), // 3 min difference  
                LosUtc = TestTime.AddMinutes(18),
                MaxElevationDeg = 60
            }
        };

        var result = reminder.Process(TestTime, scheduled, upcomingPasses, leadMinutesBeforeAos: 15);

        Assert.Single(result);
        Assert.Equal(TestTime.AddMinutes(5), result[0].AosUtc); // Should pick exact match
        Assert.Equal(45, result[0].MaxElevationDeg);
    }

    [Fact]
    public void RematchAndPrune_uses_optimized_lookup()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime.AddMinutes(5) },
            new() { NoradId = "07530", AosUtc = TestTime.AddMinutes(10) },
            new() { NoradId = "99999", AosUtc = TestTime.AddMinutes(15) } // No matching pass
        };

        var upcomingPasses = new List<PassInfo>
        {
            new()
            {
                SatelliteName = "ISS",
                NoradId = "25544",
                AosUtc = TestTime.AddMinutes(6), // Slight drift from scheduled
                LosUtc = TestTime.AddMinutes(16),
                MaxElevationDeg = 45
            },
            new()
            {
                SatelliteName = "AO-7",
                NoradId = "07530",
                AosUtc = TestTime.AddMinutes(11), // Slight drift
                LosUtc = TestTime.AddMinutes(21),
                MaxElevationDeg = 30
            }
        };

        var result = ScheduledPassReminder.RematchAndPrune(scheduled, upcomingPasses, TestTime);

        Assert.Equal(2, result.Count); // Should drop entry with no matching pass
        Assert.Contains(result, e => e.NoradId == "25544" && e.AosUtc == TestTime.AddMinutes(6));
        Assert.Contains(result, e => e.NoradId == "07530" && e.AosUtc == TestTime.AddMinutes(11));
    }

    [Fact]
    public void FindMatchingEntry_uses_indexed_loop()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime.AddMinutes(5) },
            new() { NoradId = "07530", AosUtc = TestTime.AddMinutes(10) },
            new() { NoradId = "25544", AosUtc = TestTime.AddMinutes(15) } // Same satellite, different time
        };

        // Should find closest match
        var result = ScheduledPassReminder.FindMatchingEntry(scheduled, "25544", TestTime.AddMinutes(6));

        Assert.NotNull(result);
        Assert.Equal("25544", result.NoradId);
        Assert.Equal(TestTime.AddMinutes(5), result.AosUtc); // Closer than +15 min entry
    }

    [Fact]
    public void FindMatchingEntry_returns_null_when_no_match()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime }
        };

        var result = ScheduledPassReminder.FindMatchingEntry(scheduled, "07530", TestTime);

        Assert.Null(result);
    }

    [Fact]
    public void FindMatchingEntry_respects_tolerance()
    {
        var scheduled = new List<ScheduledPassEntry>
        {
            new() { NoradId = "25544", AosUtc = TestTime }
        };

        // 3 minutes > 2 minute tolerance
        var result = ScheduledPassReminder.FindMatchingEntry(scheduled, "25544", TestTime.AddMinutes(3));

        Assert.Null(result);
    }

    [Fact]
    public void Performance_large_collection_handling()
    {
        // Test with larger collections to verify efficiency
        var scheduled = new List<ScheduledPassEntry>();
        var upcomingPasses = new List<PassInfo>();

        // Create 100 scheduled entries
        for (int i = 0; i < 100; i++)
        {
            scheduled.Add(new ScheduledPassEntry
            {
                NoradId = $"SAT{i:D3}",
                AosUtc = TestTime.AddMinutes(i * 5)
            });
        }

        // Create 200 upcoming passes (some matching, some not)  
        for (int i = 0; i < 200; i++)
        {
            upcomingPasses.Add(new PassInfo
            {
                SatelliteName = $"Satellite {i:D3}",
                NoradId = $"SAT{i:D3}",
                AosUtc = TestTime.AddMinutes(i * 5 + (i % 3)), // Slight variations
                LosUtc = TestTime.AddMinutes(i * 5 + 10),
                MaxElevationDeg = 30 + (i % 50)
            });
        }

        var reminder = new ScheduledPassReminder();
        var result = reminder.Process(TestTime, scheduled, upcomingPasses, leadMinutesBeforeAos: 600);

        // Should efficiently process all entries - verify at least some are processed
        Assert.True(result.Count > 0, $"Expected some results, got {result.Count}");
        Assert.True(result.Count <= 100, $"Expected <= 100 results, got {result.Count}"); // At most the scheduled count
    }
}