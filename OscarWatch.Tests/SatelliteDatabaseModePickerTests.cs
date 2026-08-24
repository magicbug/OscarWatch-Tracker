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
    public void ToActivationHints_uses_operating_modes_and_uplink_downlink_for_ssb()
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

        Assert.Equal("LSB", hints.UplinkMode);
        Assert.Equal("USB", hints.DownlinkMode);
        Assert.Equal(145.95265, hints.UplinkMhz);
        Assert.Equal(435.85045, hints.DownlinkMhz);
        Assert.Equal("up", hints.UplinkMhzDirection);
        Assert.Equal("down", hints.DownlinkMhzDirection);
        Assert.Equal("USB", SatelliteDatabaseModePicker.ResolveDefaultActivationMode(
            hints.UplinkMode,
            hints.DownlinkMode,
            hasUplink: true,
            hasDownlink: true));
    }

    [Fact]
    public void ToActivationHints_uses_fm_mode_for_fm_satellites()
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

        Assert.Equal("FM", hints.UplinkMode);
        Assert.Equal("FM", hints.DownlinkMode);
        Assert.Equal(145.85, hints.UplinkMhz);
        Assert.Equal(436.795, hints.DownlinkMhz);
        Assert.Equal("up", hints.UplinkMhzDirection);
        Assert.Equal("down", hints.DownlinkMhzDirection);
    }

    [Fact]
    public void ToActivationHints_uses_beacon_downlink_mode()
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

        Assert.Equal("CW", hints.UplinkMode);
        Assert.Equal("CW", hints.DownlinkMode);
        Assert.Null(hints.UplinkMhz);
        Assert.Equal(435.795, hints.DownlinkMhz);
        Assert.Null(hints.UplinkMhzDirection);
        Assert.Equal("down", hints.DownlinkMhzDirection);
    }

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
