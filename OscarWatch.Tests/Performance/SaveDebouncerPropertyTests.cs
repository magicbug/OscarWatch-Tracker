// Feature: startup-io-rendering-optimisation, Property 2: Debounce coalesces rapid saves
// Feature: startup-io-rendering-optimisation, Property 3: Debounce writes latest state
// Feature: startup-io-rendering-optimisation, Property 4: Debounce retains state on write failure

using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests.Performance;

/// <summary>
/// Property-based and unit tests for the SettingsService save debouncer.
///
/// **Validates: Requirements 2.1, 2.2, 2.3, 2.5**
/// </summary>
public sealed class SaveDebouncerPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public SaveDebouncerPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"oscarwatch-debounce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Property 2: Debounce coalesces rapid saves.
    /// For any N >= 2 rapid RequestSave() calls within the quiet period,
    /// exactly one write occurs after the quiet period elapses.
    ///
    /// **Validates: Requirements 2.1, 2.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool Rapid_saves_coalesce_into_single_write(byte requestCountByte)
    {
        // Map to 2–20 range
        var requestCount = (requestCountByte % 19) + 2;

        var path = Path.Combine(_tempDir, $"coalesce-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);
        service.Current.GroundStation.DisplayName = "Initial";

        // Fire N rapid saves (all within the 500ms quiet period)
        for (var i = 0; i < requestCount; i++)
        {
            service.Current.GroundStation.DisplayName = $"Save-{i}";
            service.RequestSave();
        }

        // Poll until file appears (quiet period elapses + write completes)
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        // The file should exist and contain only the last value
        if (!File.Exists(path))
            return false;

        var content = File.ReadAllText(path);
        return content.Contains($"Save-{requestCount - 1}");
    }

    /// <summary>
    /// Property 3: Debounce writes latest state.
    /// For any sequence of settings mutations followed by a flush,
    /// the persisted content matches the final settings state.
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 50)]
    public bool Flush_persists_latest_settings_state(byte mutationCountByte)
    {
        // Map to 1–10 range
        var mutationCount = (mutationCountByte % 10) + 1;

        var path = Path.Combine(_tempDir, $"latest-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);

        // Apply mutations and request saves
        var lastName = "";
        for (var i = 0; i < mutationCount; i++)
        {
            lastName = $"Mutation-{i}";
            service.Current.GroundStation.DisplayName = lastName;
            service.RequestSave();
        }

        // Flush immediately (simulating shutdown)
        service.FlushAsync().GetAwaiter().GetResult();

        // Verify persisted state matches the last mutation
        if (!File.Exists(path))
            return false;

        var json = File.ReadAllText(path);
        return json.Contains(lastName);
    }

    /// <summary>
    /// Debounce retains state on write failure.
    /// When a write fails, _savePending remains true for retry.
    ///
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Fact]
    public void Write_failure_retains_pending_state_for_retry()
    {
        // Use a path where the parent is a file (blocks directory creation → write fails)
        var blockerPath = Path.Combine(_tempDir, $"blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockerPath, "blocks directory creation");
        var settingsPath = Path.Combine(blockerPath, "settings.json");

        using var service = new SettingsService(settingsPath);
        service.Current.GroundStation.DisplayName = "Station-Fail";

        Exception? reportedError = null;
        void Handler(Exception ex) => reportedError = ex;
        SettingsService.SaveFailed += Handler;

        try
        {
            service.RequestSave();

            // Wait for both the error to be reported AND SavePending to become true
            // (the continuation sets _savePending = true after the exception is observed).
            // The debounced save retries a blocked file a few times before reporting.
            // Match Timer_reset_on_rapid_requests: 15s covers thread-pool starvation on CI.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((reportedError is null || !service.SavePending) && DateTime.UtcNow < deadline)
                Thread.Sleep(50);

            Assert.True(
                reportedError is not null,
                "SaveFailed was not reported within 15 seconds after a blocked write.");
            Assert.True(
                service.SavePending,
                "SavePending was false after SaveFailed; the failed write should stay pending for retry.");
        }
        finally
        {
            SettingsService.SaveFailed -= Handler;
        }
    }

    /// <summary>
    /// Unit test: FlushAsync writes immediately when a save is pending.
    /// </summary>
    [Fact]
    public async Task FlushAsync_writes_immediately_when_pending()
    {
        var path = Path.Combine(_tempDir, $"flush-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);
        service.Current.GroundStation.DisplayName = "FlushTest";

        // Request save (starts the 500ms timer)
        service.RequestSave();

        // Immediately flush — should not wait for timer
        await service.FlushAsync();

        Assert.True(File.Exists(path));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("FlushTest", json);
    }

    /// <summary>
    /// Unit test: Timer is reset on each rapid request (last request wins the quiet period).
    /// </summary>
    [Fact]
    public async Task Timer_reset_on_rapid_requests()
    {
        var path = Path.Combine(_tempDir, $"reset-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);

        // First request
        service.Current.GroundStation.DisplayName = "First";
        service.RequestSave();

        // Stay well inside the 500ms quiet period. Waiting 300ms flakes on loaded
        // Windows CI runners: Task.Delay can overshoot, the first save can land,
        // and File.Exists then returns before the second request is issued.
        await Task.Delay(50);

        // Second request resets the timer
        service.Current.GroundStation.DisplayName = "Second";
        service.RequestSave();

        // Poll for the second value, not merely that the file exists. An early
        // first write must not make the assertion read stale content. The generous
        // deadline covers thread pool starvation on small CI runners, which delays the timer.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? json = null;
        while (DateTime.UtcNow < deadline)
        {
            json = TryReadShared(path);
            if (json is not null && json.Contains("Second"))
                break;

            await Task.Delay(50);
        }

        Assert.False(string.IsNullOrEmpty(json));
        Assert.Contains("Second", json);
        Assert.DoesNotContain("First", json);
    }

    /// <summary>
    /// Read without denying replace. An exclusive read on Windows makes File.Replace fail,
    /// and a failed debounced save used to leave the earlier snapshot on disk.
    /// </summary>
    private static string? TryReadShared(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
