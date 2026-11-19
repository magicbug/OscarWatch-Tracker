using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class SatelliteDatabaseEditorViewModelTests
{
    [Fact]
    public void MaxFrequencyKHz_accepts_QO100_and_all_bundled_database_frequencies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var qo100 = Assert.Single(entries, e => e.Name == "QO-100");
        Assert.Contains(qo100.Modes, m => m.DownlinkKHz >= 10_489_500 && m.UplinkKHz >= 2_400_040);

        foreach (var entry in entries)
        {
            foreach (var mode in entry.Modes)
            {
                Assert.True(
                    (decimal)mode.DownlinkKHz <= SatelliteDatabaseEditorViewModel.MaxFrequencyKHz,
                    $"{entry.Name} ({mode.Type}) downlink {mode.DownlinkKHz} kHz exceeds editor MaxFrequencyKHz.");
                Assert.True(
                    (decimal)mode.UplinkKHz <= SatelliteDatabaseEditorViewModel.MaxFrequencyKHz,
                    $"{entry.Name} ({mode.Type}) uplink {mode.UplinkKHz} kHz exceeds editor MaxFrequencyKHz.");
            }
        }
    }

    [Theory]
    [InlineData(2_400_245.0)]
    [InlineData(10_489_745.0)]
    [InlineData(10_495_000.0)]
    [InlineData(24_050_000.0)]
    [InlineData(47_088_000.0)]
    [InlineData(241_000_000.0)]
    public void MaxFrequencyKHz_accepts_microwave_satellite_bands(decimal frequencyKHz)
    {
        Assert.True(frequencyKHz <= SatelliteDatabaseEditorViewModel.MaxFrequencyKHz);
    }

    [Fact]
    public void Editor_frequency_inputs_are_not_capped_below_QO100()
    {
        var xaml = File.ReadAllText(FindRepoFile("OscarWatch", "Views", "SatelliteDatabaseWindow.axaml"));
        Assert.DoesNotContain("Maximum=\"1300000\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "Maximum=\"{x:Static vm:SatelliteDatabaseEditorViewModel.MaxFrequencyKHz}\""));

        // The previous spinner cap was 1_300_000 kHz (1.3 GHz), below both QO-100 legs.
        const decimal previousCapKHz = 1_300_000m;
        Assert.True(2_400_245m > previousCapKHz);
        Assert.True(10_489_745m > previousCapKHz);
        Assert.True(2_400_245m <= SatelliteDatabaseEditorViewModel.MaxFrequencyKHz);
        Assert.True(10_489_745m <= SatelliteDatabaseEditorViewModel.MaxFrequencyKHz);
    }

    [Fact]
    public void Editing_and_saving_custom_QO100_transponder_preserves_ghz_frequencies()
    {
        var editor = new StubDatabaseEditor(
        [
            new SatelliteRadioEntry
            {
                Name = "QO-100",
                NoradId = "43700",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Narrowband Transponder",
                        DownlinkKHz = 10_489_745,
                        UplinkKHz = 2_400_245,
                        DownlinkMode = "USB",
                        UplinkMode = "USB",
                        Doppler = "NOR"
                    }
                ]
            }
        ]);

        var vm = new SatelliteDatabaseEditorViewModel(
            editor,
            new StubSyncService(),
            new StubTleService(),
            LocalizationService.Instance);

        Assert.Equal(10_489_745, vm.DownlinkKHz);
        Assert.Equal(2_400_245, vm.UplinkKHz);

        vm.AddModeCommand.Execute(null);
        vm.ModeType = "Custom CW";
        vm.DownlinkKHz = 10_489_550.25;
        vm.UplinkKHz = 2_400_050.25;
        vm.DownlinkMode = "CW";
        vm.UplinkMode = "CW";

        Assert.True(vm.TrySave(out var error), error);
        Assert.Null(error);

        var saved = Assert.Single(editor.SavedEntries!);
        var custom = Assert.Single(saved.Modes, m => m.Type == "Custom CW");
        Assert.Equal(10_489_550.25, custom.DownlinkKHz);
        Assert.Equal(2_400_050.25, custom.UplinkKHz);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OscarWatch.slnx")))
                return Path.Combine([dir.FullName, .. parts]);

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class StubDatabaseEditor(List<SatelliteRadioEntry> initial) : ISatelliteDatabaseEditor
    {
        public string BundledPath => "bundled.json";
        public string UserPath => "user.json";
        public string ActivePath => "bundled.json";
        public bool IsUsingUserDatabase => false;
        public IReadOnlyList<SatelliteRadioEntry>? SavedEntries { get; private set; }

        public List<SatelliteRadioEntry> LoadForEditing() => initial;

        public void Save(IReadOnlyList<SatelliteRadioEntry> entries)
        {
            var error = SatelliteDatabaseFile.ValidateEntries(entries);
            if (error is not null)
                throw new InvalidOperationException(error);

            SavedEntries = entries.ToList();
        }

        public void ResetToBundled() { }
    }

    private sealed class StubSyncService : ISatelliteDatabaseSyncService
    {
        public Task<SatelliteDatabaseMergePlan> FetchMergePlanAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ApplyMerge(SatelliteDatabaseMergePlan plan, SatelliteDatabaseMergeSelection selection) =>
            throw new NotSupportedException();

        public void SaveMergeAcknowledgments(SatelliteDatabaseMergePlan plan, SatelliteDatabaseMergeSelection selection) =>
            throw new NotSupportedException();

        public List<SatelliteRadioEntry> LoadLocalEntriesForMerge() => [];

        public void SaveMergedEntries(IReadOnlyList<SatelliteRadioEntry> merged) { }
    }

    private sealed class StubTleService : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => [];
        public DateTime? LastFetchedUtc => null;
        public string CachePath => "";
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public bool IsStale(int staleHours) => false;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public string ActiveSourceLabel => "";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => [];
    }
}
