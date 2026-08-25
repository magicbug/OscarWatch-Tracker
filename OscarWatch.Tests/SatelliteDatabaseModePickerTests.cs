using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SatelliteDatabaseModePickerTests
{
    [Fact]
    public void ResolveDefaultMode_uses_saved_mode_index()
    {
        var database = new StubDatabase(
        [
            new SatelliteRadioEntry
            {
                Name = "AO-07",
                NoradId = "7530",
                Modes =
                [
                    new SatelliteTransponderMode { Type = "Mode B", DownlinkKHz = 145950, UplinkKHz = 432150 },
                    new SatelliteTransponderMode { Type = "Mode A", DownlinkKHz = 145850, UplinkKHz = 432100 }
                ]
            }
        ]);

        var selections = new Dictionary<string, SatelliteFrequencySelection>
        {
            ["AO-07"] = new() { ModeIndex = 1, ModeType = "Mode B" }
        };

        var mode = SatelliteDatabaseModePicker.ResolveDefaultMode(database, "AO-07", "7530", selections);

        Assert.NotNull(mode);
        Assert.Equal("Mode A", mode.Type);
    }

    [Fact]
    public void ToActivationHints_maps_ssb_satellite_to_api_modes()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            DownlinkKHz = 435850.45,
            UplinkKHz = 145952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var hints = SatelliteDatabaseModePicker.ToActivationHints(mode);

        Assert.Equal(HamsAtApiModes.Ssb, hints.SuggestedMode);
        Assert.Equal(HamsAtApiModes.Linear, hints.AvailableModes);
        Assert.Equal(145.95265, hints.UplinkMhz);
        Assert.Equal(435.85045, hints.DownlinkMhz);
    }

    [Fact]
    public void ToActivationHints_maps_fm_satellite_to_fm_only()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            DownlinkKHz = 436795,
            UplinkKHz = 145850,
            DownlinkMode = "FMN",
            UplinkMode = "FMN",
            Doppler = "REV"
        };

        var hints = SatelliteDatabaseModePicker.ToActivationHints(mode);

        Assert.Equal(HamsAtApiModes.Fm, hints.SuggestedMode);
        Assert.Equal(HamsAtApiModes.FmOnly, hints.AvailableModes);
        Assert.Equal(145.85, hints.UplinkMhz);
        Assert.Equal(436.795, hints.DownlinkMhz);
    }

    [Fact]
    public void ToActivationHints_maps_beacon_to_cw()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "CW Beacon",
            DownlinkKHz = 435795,
            UplinkKHz = 0,
            DownlinkMode = "CW",
            UplinkMode = "CW",
            Doppler = "REV"
        };

        var hints = SatelliteDatabaseModePicker.ToActivationHints(mode);

        Assert.Equal(HamsAtApiModes.Cw, hints.SuggestedMode);
        Assert.Equal(HamsAtApiModes.CwOnly, hints.AvailableModes);
        Assert.Null(hints.UplinkMhz);
        Assert.Equal(435.795, hints.DownlinkMhz);
    }

    [Theory]
    [InlineData("USB", "SSB")]
    [InlineData("LSB", "SSB")]
    [InlineData("CW", "CW")]
    [InlineData("FMN", "FM")]
    [InlineData("DATA-USB", "Data")]
    public void ToApiMode_maps_cat_modes(string input, string expected) =>
        Assert.Equal(expected, SatelliteDatabaseModePicker.ToApiMode(input));

    private sealed class StubDatabase(IReadOnlyList<SatelliteRadioEntry> entries) : ISatelliteDatabaseService
    {
        public IReadOnlyList<SatelliteRadioEntry> Entries { get; } = entries;
        public string ActiveDatabasePath { get; } = "stub.json";
        public bool IsUsingUserDatabase => false;

        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null)
        {
            return Entries.FirstOrDefault(e =>
                e.Name.Equals(satelliteName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(noradId)
                    && string.Equals(e.NoradId, noradId, StringComparison.OrdinalIgnoreCase)));
        }

        public void Reload() { }
    }
}
