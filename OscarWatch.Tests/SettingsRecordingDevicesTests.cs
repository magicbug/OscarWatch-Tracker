using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class SettingsRecordingDevicesTests
{
    [Fact]
    public void Constructor_does_not_probe_recording_devices()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        var recording = new CountingAudioRecordingService();
        using var vm = CreateViewModel(recording);

        Assert.Equal(0, recording.TryInitializeCount);
        Assert.Equal(0, recording.GetInputDevicesCount);
        Assert.False(vm.RecordingDevicesLoaded);
        Assert.True(vm.RecordingAvailable);
    }

    [Fact]
    public async Task OnRecordingTabSelected_loads_devices_off_ui_thread()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        var recording = new CountingAudioRecordingService();
        using var vm = CreateViewModel(recording);

        vm.OnRecordingTabSelected();
        await WaitForRecordingDevicesLoadedAsync(vm);

        Assert.Equal(1, recording.TryInitializeCount);
        Assert.Equal(1, recording.GetInputDevicesCount);
        Assert.True(vm.RecordingDevicesLoaded);
        Assert.True(vm.RecordingAvailable);
        Assert.Single(vm.RecordingDeviceOptions);
        Assert.Equal("Fake Input", vm.RecordingDeviceOptions[0].DisplayName);
    }

    [Fact]
    public async Task RefreshRecordingDevicesCommand_reloads_device_list()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        var recording = new CountingAudioRecordingService();
        using var vm = CreateViewModel(recording);

        await vm.RefreshRecordingDevicesCommand.ExecuteAsync(null);
        await WaitForRecordingDevicesLoadedAsync(vm);

        Assert.Equal(1, recording.TryInitializeCount);
        Assert.Equal(1, recording.GetInputDevicesCount);
        Assert.True(vm.RecordingDevicesLoaded);
    }

    [Fact]
    public void Constructor_shows_saved_device_without_enumerating()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        var settings = new TestSettingsService();
        settings.Current.PassRecording = new PassRecordingSettings
        {
            DeviceId = "DAX Audio RX 1",
            DeviceDisplayName = "DAX Audio RX 1"
        };
        var recording = new CountingAudioRecordingService();
        using var vm = CreateViewModel(recording, settings);

        Assert.Equal(0, recording.GetInputDevicesCount);
        Assert.Equal("DAX Audio RX 1", vm.SelectedRecordingDevice?.DisplayName);
        Assert.Single(vm.RecordingDeviceOptions);
    }

    private static SettingsViewModel CreateViewModel(
        IAudioRecordingService recording,
        TestSettingsService? settings = null)
    {
        settings ??= new TestSettingsService();
        return new SettingsViewModel(
            settings,
            LocalizationService.Instance,
            new StubSpeechService(),
            new StubAlertSoundService(),
            recording,
            new StubCloudlogRadioSyncService(),
            new StubCloudlogLookupService(),
            new StubHamsAtRovesService(),
            new StubGpsService(),
            new StubSatelliteLinkBroadcastService(),
            new StubSatelliteStatusReportService());
    }

    private static async Task WaitForRecordingDevicesLoadedAsync(SettingsViewModel vm)
    {
        // Wait until the refresh finishes (!Loading) so options are populated, not merely flagged loaded.
        for (var attempt = 0;
             attempt < 100 && !(vm.RecordingDevicesLoaded && !vm.RecordingDevicesLoading);
             attempt++)
            await Task.Delay(50);
    }

    private sealed class CountingAudioRecordingService : IAudioRecordingService
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsRecording => false;
        public string? ActiveNoradId => null;
        public string? ActiveOutputPath => null;
        public string? LastCompletedOutputPath => null;
        public int TryInitializeCount { get; private set; }
        public int GetInputDevicesCount { get; private set; }

        public bool TryInitialize()
        {
            TryInitializeCount++;
            return true;
        }

        public IReadOnlyList<AudioInputDevice> GetInputDevices()
        {
            GetInputDevicesCount++;
            return [new AudioInputDevice("Fake Input", "Fake Input")];
        }

        public Task StartAsync(
            string noradId,
            string satelliteName,
            string deviceId,
            RecordingFormatPreset format,
            string outputPath,
            string? deviceName = null,
            RecordingContainerFormat container = RecordingContainerFormat.Wav,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-test-settings.json");
        public string SerializeCurrent() => "{}";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Load() { }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RequestSave() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SyncGridFromLatLon() { }
        public void SyncLatLonFromGrid() { }
        public void EnsureSavedStations() { }
        public void ApplyActiveStation() { }
        public void SyncActiveStationFromGroundStation() { }
    }

    private sealed class StubSpeechService : ISpeechService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<SpeechVoiceOption> GetAvailableVoices() => [];
        public Task SpeakAsync(string text, string? voiceName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAlertSoundService : IAlertSoundService
    {
        public void PlayAlert() { }
    }

    private sealed class StubCloudlogRadioSyncService : ICloudlogRadioSyncService
    {
        public event Action? StateChanged;
        public string? LastError => null;
        public DateTimeOffset? LastSuccessUtc => null;
        public void Publish(CloudlogSettings settings, CloudlogRadioUpdate? update) { }
        public void ResetThrottle() { }
        public Task<bool> TestConnectionAsync(CloudlogSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubCloudlogLookupService : ICloudlogLookupService
    {
        public bool CanCheckGrids(CloudlogSettings settings) => false;
        public Task<CloudlogLogbooksResult> FetchLogbooksAsync(
            CloudlogSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudlogLogbooksResult());
        public Task<CloudlogGridCheckResult?> CheckGridWorkedAsync(
            CloudlogSettings settings,
            string grid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudlogGridCheckResult?>(null);
        public bool CanUploadQsos(CloudlogSettings settings) => false;
        public Task<CloudlogStationProfilesResult> FetchStationProfilesAsync(
            CloudlogSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudlogStationProfilesResult());
    }

    private sealed class StubHamsAtRovesService : IHamsAtRovesService
    {
        public Task<HamsAtFetchResult> FetchUpcomingAsync(
            HamsAtSettings settings,
            bool bypassCache = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(HamsAtFetchResult.Success([]));
        public Task<(bool Ok, string Message)> TestConnectionAsync(
            HamsAtSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "ok"));
    }

    private sealed class StubGpsService : IGpsService
    {
        public void Update(GpsSettings settings) { }
        public void Disconnect() { }
        public void DisconnectAndWait() { }
        public GpsConnectionStatus GetStatus() =>
            new(false, false, null, null, null, null, null, null);
        public DateTime? GetTrackingUtc() => null;
        public void Dispose() { }
    }

    private sealed class StubSatelliteLinkBroadcastService : ISatelliteLinkBroadcastService
    {
        public event Action? StateChanged;
        public bool IsListening => false;
        public int ClientCount => 0;
        public string? LastError => null;
        public void ApplySettings(SatelliteLinkSettings settings) { }
        public void Publish(SatelliteTrackState? track, RigTrackingContext? context, bool force = false) { }
        public void PublishQso(QsoRecord record, QsoLogbook logbook, SatelliteLinkQsoEventKind kind, string? noradId = null) { }
        public Task<bool> TestBindAsync(SatelliteLinkSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class StubSatelliteStatusReportService : ISatelliteStatusReportService
    {
        public Task<SatelliteStatusTokenTestResult> TestTokenAsync(
            SatelliteStatusSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SatelliteStatusTokenTestResult(true, "ok", 200));

        public Task<SatelliteStatusReportResult> SubmitReportAsync(
            SatelliteStatusSettings settings,
            SatelliteStatusReportRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SatelliteStatusReportResult(true, true, "ok", 201));

        public Task<SatelliteStatusFetchResult> FetchCommunityAsync(
            SatelliteStatusSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SatelliteStatusFetchResult(true, false, null, "ok", 200));
    }
}
