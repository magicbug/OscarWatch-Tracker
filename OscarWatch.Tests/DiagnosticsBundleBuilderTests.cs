using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Diagnostics;

namespace OscarWatch.Tests;

public sealed class DiagnosticsBundleBuilderTests
{
    [Fact]
    public void RedactSettings_masks_api_keys()
    {
        var settings = new AppSettings
        {
            Cloudlog = new CloudlogSettings { ApiKey = "secret-cloudlog" },
            HamsAt = new HamsAtSettings { ApiKey = "secret-hamsat" },
            SatelliteStatus = new SatelliteStatusSettings { ApiToken = "secret-sat-status" },
            Qrz = new QrzSettings { Username = "MM9SQL", Password = "secret-qrz" },
            HamQth = new HamQthSettings { Username = "MM9SQL", Password = "secret-hamqth" }
        };

        var redacted = DiagnosticsBundleBuilder.RedactSettings(settings);

        Assert.DoesNotContain("secret-cloudlog", redacted);
        Assert.DoesNotContain("secret-hamsat", redacted);
        Assert.DoesNotContain("secret-sat-status", redacted);
        Assert.DoesNotContain("secret-qrz", redacted);
        Assert.DoesNotContain("secret-hamqth", redacted);
        Assert.Contains("\"***\"", redacted);
    }

    [Fact]
    public void ReadSharedLogLines_reads_while_file_is_open_for_writing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-log-read-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine, Enumerable.Range(1, 5).Select(i => $"line {i}")));

            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);

            var lines = DiagnosticsBundleBuilder.ReadSharedLogLines(path);

            Assert.Equal(5, lines.Count);
            Assert.Equal("line 5", lines[^1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void FormatLogTail_returns_last_n_lines()
    {
        var lines = Enumerable.Range(1, 250).Select(i => $"line {i}").ToList();
        var tail = DiagnosticsBundleBuilder.FormatLogTail(lines, maxLines: 200);

        Assert.StartsWith("line 51", tail);
        Assert.EndsWith("line 250", tail);
        Assert.DoesNotContain("line 50", tail);
    }

    [Fact]
    public void Build_includes_english_rig_and_rotator_status()
    {
        var rig = new StubRigController(new RigConnectionStatus(
            false,
            false,
            RigStatusKind.NotConnected,
            "COM3",
            "Access denied",
            null,
            null));
        var rotator = new StubRotatorController(new RotatorPositionStatus(
            false,
            null,
            null,
            ConnectionKind: RotatorConnectionKind.ConnectFailed,
            ConnectionDetail: "Port busy"));

        var bundle = DiagnosticsBundleBuilder.Build(new StubSettingsService(), rig, rotator);

        Assert.Contains("Rig not connected (COM3): Access denied", bundle);
        Assert.Contains("Rotator not connected: Port busy", bundle);
        Assert.Contains("== Settings (redacted) ==", bundle);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "diagnostics-bundle-test.json");
        public string SerializeCurrent() => "{}";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class StubRigController(RigConnectionStatus status) : IRigController
    {
        public RigConnectionStatus GetStatus() => status;
        public void PublishContext(RigSettings settings, RigTrackingContext? context, bool reinitializePass = false, bool? catPausedOverride = null) { }
        public void Update(RigSettings settings, RigTrackingContext? context) { }
        public void ApplySelectedCtcss(RigSettings settings, RigTrackingContext? context) { }
        public void SetPtt(bool transmit) { }
        public void SetHandshakePtt(bool useRts, bool assert) { }
        public void SetFt4SlotGatedDoppler(bool hold) { }
        public void ForceFt4DopplerStep() { }
        public bool TryForceFt4DopplerStep(DateTime frequencyAtUtc, TimeSpan timeout) => true;
        public bool TryGetUplinkRfPowerWatts(out double watts)
        {
            watts = 0;
            return false;
        }
        public bool TrySetUplinkRfPowerWatts(double watts) => false;
        public void RequireIdentityRecheck() { }
        public void Disconnect() { }
        public void DisconnectAndWait() { }
    }

    private sealed class StubRotatorController(RotatorPositionStatus status) : IRotatorController
    {
        public RotatorPositionStatus GetPositionStatus() => status;
        public void Update(RotatorSettings settings, SatelliteTrackState? target) { }
        public void Park(RotatorSettings settings) { }
        public void MoveTo(double azimuthDeg, double elevationDeg, RotatorSettings settings) { }
        public void Stop(RotatorSettings settings) { }
        public void ResumeTracking(RotatorSettings settings) { }
        public void SetStandby(bool active, RotatorSettings settings) { }
        public void Disconnect() { }
        public void DisconnectAndWait() { }
        public void SetActivePass(PassInfo? pass) { }
    }
}
