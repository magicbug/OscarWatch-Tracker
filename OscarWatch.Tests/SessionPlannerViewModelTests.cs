using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.SessionPlanner;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

/// <summary>
/// Unit tests for SessionPlannerViewModel state transitions.
/// Uses minimal stubs — tests focus on wiring and state machine logic.
/// </summary>
public sealed class SessionPlannerViewModelTests
{
    // ─── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubLiveTrackingService : ILiveTrackingService
    {
        public string? FocusedNoradId { get; set; }
        public DateTime SnapshotUtc => DateTime.MinValue;
        public TimeSpan MapTimeOffset { get; set; }
        public DateTime LiveNowSnapshotUtc => DateTime.MinValue;
        public IReadOnlyList<SatelliteTrackState> GetSnapshot() => [];
        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => [];
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "";
        public string SerializeCurrent() => "";
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

    private sealed class StubLocalizationService : ILocalizationService
    {
        public string Get(string key) => key;
        public string Get(string key, params object[] args) => key;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static SessionPlannerViewModel CreateViewModel(out PlanExecutor executor)
    {
        var liveTracking = new StubLiveTrackingService();
        executor = new PlanExecutor(liveTracking);
        var settings = new StubSettingsService();
        var localization = new StubLocalizationService();

        // SessionPlannerService is not needed for state transition tests — pass null via reflection workaround.
        // The ViewModel constructor requires it but we won't call GeneratePlanAsync.
        var vm = new SessionPlannerViewModel(null!, executor, settings, localization);
        vm.Initialize();
        return vm;
    }

    private static SessionPlan BuildPastPlan()
    {
        var yesterday = DateTime.UtcNow.AddDays(-1);
        var sessionStart = yesterday;
        var sessionEnd = yesterday.AddHours(2);

        var pass = new PassInfo
        {
            SatelliteName = "ISS",
            NoradId = "25544",
            AosUtc = yesterday.AddMinutes(10),
            LosUtc = yesterday.AddMinutes(20),
            MaxElevationDeg = 45.0,
            MaxElevationUtc = yesterday.AddMinutes(15)
        };

        var scored = new ScoredPass
        {
            Pass = pass,
            QualityScore = 0.8,
            SatellitePriority = 1,
            CompositeScore = 8.0
        };

        var scheduled = new ScheduledPass
        {
            Scored = scored,
            Reason = PassSelectionReason.AlgorithmSelected
        };

        return new SessionPlan
        {
            SessionStartUtc = sessionStart,
            SessionEndUtc = sessionEnd,
            ScheduledPasses = [scheduled],
            AllCandidates = [scored],
            ExcludedIds = new HashSet<string>(),
            ForcedInclusionIds = new HashSet<string>()
        };
    }

    // ─── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public void StartExecution_ThenPause_SetsStateToPaused()
    {
        var vm = CreateViewModel(out _);
        vm.ActivePlan = BuildPastPlan(); // Need an active plan; use a plan (review-only flag irrelevant here)

        // Override IsReviewOnly so StartExecution doesn't bail out
        vm.IsReviewOnly = false;

        vm.StartExecutionCommand.Execute(null);
        Assert.Equal(PlanExecutionState.Running, vm.ExecutionState);

        vm.PauseExecutionCommand.Execute(null);
        Assert.Equal(PlanExecutionState.Paused, vm.ExecutionState);
    }

    [Fact]
    public void Pause_ThenResume_SetsStateBackToRunning()
    {
        var vm = CreateViewModel(out _);
        vm.ActivePlan = BuildPastPlan();
        vm.IsReviewOnly = false;

        vm.StartExecutionCommand.Execute(null);
        vm.PauseExecutionCommand.Execute(null);
        Assert.Equal(PlanExecutionState.Paused, vm.ExecutionState);

        vm.ResumeExecutionCommand.Execute(null);
        Assert.Equal(PlanExecutionState.Running, vm.ExecutionState);
    }

    [Fact]
    public void PlanWithPastPass_AfterTick_StateBecomesCompleted()
    {
        var vm = CreateViewModel(out _);
        var plan = BuildPastPlan();
        vm.ActivePlan = plan;
        vm.IsReviewOnly = false;

        vm.StartExecutionCommand.Execute(null);
        Assert.Equal(PlanExecutionState.Running, vm.ExecutionState);

        // Tick via ViewModel — since the only pass's LOS is in the past,
        // the executor will transition to Completed and the ViewModel syncs state.
        vm.Tick();

        Assert.Equal(PlanExecutionState.Completed, vm.ExecutionState);
    }

    [Fact]
    public void LoadPlan_WithPastSessionWindow_SetsIsReviewOnlyTrue()
    {
        var vm = CreateViewModel(out _);
        var pastPlan = BuildPastPlan();

        // Serialize then load via the LoadPlan command (simulates file load)
        var json = SessionPlanPersistence.Serialise(pastPlan);
        vm.LoadPlanCommand.Execute(json);

        Assert.True(vm.IsReviewOnly);
    }
}
