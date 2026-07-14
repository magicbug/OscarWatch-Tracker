using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Export;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.SessionPlanner;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class SessionPlannerViewModel : ViewModelBase
{
    private readonly SessionPlannerService _plannerService;
    private readonly PlanExecutor _executor;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;

    public ObservableCollection<SessionPlanPassRow> ScheduledPasses { get; } = [];

    [ObservableProperty]
    private DateTime _sessionStartTime;

    [ObservableProperty]
    private DateTime _sessionEndTime;

    /// <summary>Date portion of session start for CalendarDatePicker binding.</summary>
    public DateTime? SessionStartDate
    {
        get => SessionStartTime.Date;
        set
        {
            if (value is not null)
            {
                SessionStartTime = value.Value.Date + SessionStartTime.TimeOfDay;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Time-of-day portion of session start for TimePicker binding.</summary>
    public TimeSpan? SessionStartTimeOfDay
    {
        get => SessionStartTime.TimeOfDay;
        set
        {
            if (value is not null)
            {
                SessionStartTime = SessionStartTime.Date + value.Value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Date portion of session end for CalendarDatePicker binding.</summary>
    public DateTime? SessionEndDate
    {
        get => SessionEndTime.Date;
        set
        {
            if (value is not null)
            {
                SessionEndTime = value.Value.Date + SessionEndTime.TimeOfDay;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Time-of-day portion of session end for TimePicker binding.</summary>
    public TimeSpan? SessionEndTimeOfDay
    {
        get => SessionEndTime.TimeOfDay;
        set
        {
            if (value is not null)
            {
                SessionEndTime = SessionEndTime.Date + value.Value;
                OnPropertyChanged();
            }
        }
    }

    partial void OnSessionStartTimeChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SessionStartDate));
        OnPropertyChanged(nameof(SessionStartTimeOfDay));
    }

    partial void OnSessionEndTimeChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SessionEndDate));
        OnPropertyChanged(nameof(SessionEndTimeOfDay));
    }

    [ObservableProperty]
    private SessionPlan? _activePlan;

    [ObservableProperty]
    private PlanExecutionState _executionState;

    [ObservableProperty]
    private string _currentPassDisplay = "";

    [ObservableProperty]
    private string _nextPassDisplay = "";

    [ObservableProperty]
    private string _countdownText = "";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isReviewOnly;

    [ObservableProperty]
    private bool _useUtcTime;

    [ObservableProperty]
    private bool _use24HourClock;

    public SessionPlannerViewModel(
        SessionPlannerService plannerService,
        PlanExecutor executor,
        ISettingsService settings,
        ILocalizationService localization)
    {
        _plannerService = plannerService;
        _executor = executor;
        _settings = settings;
        _localization = localization;
    }

    /// <summary>
    /// Initialises default values from settings. Call after construction.
    /// </summary>
    public void Initialize()
    {
        var current = _settings.Current;
        UseUtcTime = current.PassPlannerUseUtcTime;
        Use24HourClock = current.Use24HourClock;

        var now = UseUtcTime ? DateTime.UtcNow : DateTime.Now;
        SessionStartTime = SessionPlannerService.RoundUpTo15Minutes(now);
        SessionEndTime = SessionStartTime.AddHours(current.PassPredictionHours);

        ExecutionState = PlanExecutionState.Idle;
        StatusText = "";
    }

    [RelayCommand]
    private async Task GeneratePlanAsync(CancellationToken cancellationToken)
    {
        // Validate time window
        if (SessionEndTime <= SessionStartTime)
        {
            StatusText = _localization.Get("SessionPlanner.Error.EndBeforeStart");
            return;
        }

        if (SessionEndTime - SessionStartTime > TimeSpan.FromHours(48))
        {
            StatusText = _localization.Get("SessionPlanner.Error.MaxDurationExceeded");
            return;
        }

        StatusText = _localization.Get("SessionPlanner.Status.Generating");

        try
        {
            var startUtc = UseUtcTime ? SessionStartTime : SessionStartTime.ToUniversalTime();
            var endUtc = UseUtcTime ? SessionEndTime : SessionEndTime.ToUniversalTime();

            var plan = await _plannerService.GeneratePlanAsync(startUtc, endUtc, cancellationToken);
            ActivePlan = plan;
            IsReviewOnly = endUtc <= DateTime.UtcNow;

            RebuildScheduledPasses();

            StatusText = plan.ScheduledCount > 0
                ? _localization.Get("SessionPlanner.Status.Generated", plan.ScheduledCount)
                : _localization.Get("SessionPlanner.Status.NoPasses");
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void StartExecution()
    {
        if (ActivePlan is null || IsReviewOnly)
            return;

        var preAlertMinutes = _settings.Current.SessionPlannerPreAlertMinutes;
        _executor.Start(ActivePlan, preAlertMinutes);
        ExecutionState = _executor.State;
        UpdateDisplayProperties();
    }

    [RelayCommand]
    private void StopExecution()
    {
        _executor.Stop();
        ExecutionState = _executor.State;
        CurrentPassDisplay = "";
        NextPassDisplay = "";
        CountdownText = "";
        ProgressText = "";
    }

    [RelayCommand]
    private void PauseExecution()
    {
        _executor.Pause();
        ExecutionState = _executor.State;
    }

    [RelayCommand]
    private void ResumeExecution()
    {
        _executor.Resume();
        ExecutionState = _executor.State;
    }

    [RelayCommand]
    private void ExcludePass(SessionPlanPassRow? row)
    {
        if (row is null || ActivePlan is null)
            return;

        var excludedIds = ActivePlan.ExcludedIds.ToHashSet();
        excludedIds.Add(row.Source.Scored.Id);

        var adjusted = _plannerService.AdjustPlan(
            ActivePlan,
            excludedIds: excludedIds,
            forcedInclusionIds: ActivePlan.ForcedInclusionIds);

        ActivePlan = adjusted;
        RebuildScheduledPasses();
    }

    [RelayCommand]
    private void ForceIncludePass(SessionPlanPassRow? row)
    {
        if (row is null || ActivePlan is null)
            return;

        var forcedIds = ActivePlan.ForcedInclusionIds.ToHashSet();
        forcedIds.Add(row.Source.Scored.Id);

        var adjusted = _plannerService.AdjustPlan(
            ActivePlan,
            excludedIds: ActivePlan.ExcludedIds,
            forcedInclusionIds: forcedIds);

        ActivePlan = adjusted;
        RebuildScheduledPasses();
    }

    [RelayCommand]
    private async Task ResetAdjustments(CancellationToken cancellationToken)
    {
        if (ActivePlan is null)
            return;

        StatusText = _localization.Get("SessionPlanner.Status.Generating");

        var startUtc = ActivePlan.SessionStartUtc;
        var endUtc = ActivePlan.SessionEndUtc;

        var plan = await _plannerService.GeneratePlanAsync(startUtc, endUtc, cancellationToken);
        ActivePlan = plan;
        RebuildScheduledPasses();

        StatusText = _localization.Get("SessionPlanner.Status.Generated", plan.ScheduledCount);
    }

    /// <summary>
    /// Builds the CSV export string for the active plan.
    /// The view is responsible for presenting a file-save dialogue.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (ActivePlan is null)
            return;

        LastExportContent = SessionPlanExporter.BuildCsv(ActivePlan);
    }

    /// <summary>
    /// Builds the ICS export string for the active plan.
    /// The view is responsible for presenting a file-save dialogue.
    /// </summary>
    [RelayCommand]
    private void ExportIcs()
    {
        if (ActivePlan is null)
            return;

        var station = _settings.Current.GroundStation;
        var preAlertMinutes = _settings.Current.SessionPlannerPreAlertMinutes;
        LastExportContent = SessionPlanExporter.BuildCalendar(ActivePlan, station, preAlertMinutes);
    }

    /// <summary>
    /// Serialises the active plan to JSON. The view is responsible for writing to file.
    /// </summary>
    [RelayCommand]
    private void SavePlan()
    {
        if (ActivePlan is null)
            return;

        LastExportContent = SessionPlanPersistence.Serialise(ActivePlan);
    }

    /// <summary>
    /// Contains the last export/save result string for the view to consume.
    /// </summary>
    public string LastExportContent { get; private set; } = "";

    [RelayCommand]
    private void LoadPlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            StatusText = _localization.Get("SessionPlanner.Error.EmptyFile");
            return;
        }

        var plan = SessionPlanPersistence.Deserialise(json);
        if (plan is null)
        {
            StatusText = _localization.Get("SessionPlanner.Error.MalformedFile");
            return;
        }

        ActivePlan = plan;
        SessionStartTime = UseUtcTime ? plan.SessionStartUtc : plan.SessionStartUtc.ToLocalTime();
        SessionEndTime = UseUtcTime ? plan.SessionEndUtc : plan.SessionEndUtc.ToLocalTime();
        IsReviewOnly = plan.SessionEndUtc <= DateTime.UtcNow;

        RebuildScheduledPasses();
        StatusText = _localization.Get("SessionPlanner.Status.Loaded", plan.ScheduledCount);
    }

    /// <summary>
    /// Called by a DispatcherTimer (typically 1-second interval) from outside.
    /// Drives the executor and updates display properties.
    /// </summary>
    public void Tick()
    {
        if (_executor.State == PlanExecutionState.Idle || _executor.State == PlanExecutionState.Completed)
            return;

        _executor.Tick(DateTime.UtcNow);
        ExecutionState = _executor.State;
        UpdateDisplayProperties();
    }

    private void UpdateDisplayProperties()
    {
        var currentPass = _executor.CurrentPass;
        var nextPass = _executor.NextPass;
        var timeToNext = _executor.TimeToNextSwitch;

        // Current pass display
        if (currentPass is not null)
        {
            var elev = currentPass.Scored.Pass.MaxElevationDeg;
            CurrentPassDisplay = $"{currentPass.Scored.Pass.SatelliteName} ({elev:F0}°)";
        }
        else
        {
            CurrentPassDisplay = _localization.Get("SessionPlanner.Display.Gap");
        }

        // Next pass display
        if (nextPass is not null && timeToNext is not null)
        {
            var remaining = timeToNext.Value;
            var formatted = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{remaining.Minutes}:{remaining.Seconds:D2}";
            NextPassDisplay = $"{nextPass.Scored.Pass.SatelliteName} in {formatted}";
        }
        else
        {
            NextPassDisplay = "";
        }

        // Countdown text
        if (timeToNext is not null)
        {
            var remaining = timeToNext.Value;
            CountdownText = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{remaining.Minutes}:{remaining.Seconds:D2}";
        }
        else
        {
            CountdownText = "";
        }

        // Progress text
        if (ActivePlan is not null && ActivePlan.ScheduledCount > 0)
        {
            var total = ActivePlan.ScheduledCount;
            var completed = CountCompletedPasses();
            var currentIndex = completed + 1;
            if (currentIndex > total) currentIndex = total;

            var sessionDuration = (ActivePlan.SessionEndUtc - ActivePlan.SessionStartUtc).TotalSeconds;
            var elapsed = (DateTime.UtcNow - ActivePlan.SessionStartUtc).TotalSeconds;
            var percent = sessionDuration > 0
                ? (int)Math.Clamp(elapsed / sessionDuration * 100, 0, 100)
                : 0;

            ProgressText = $"Pass {currentIndex} of {total} ({percent}%)";
        }
        else
        {
            ProgressText = "";
        }
    }

    private int CountCompletedPasses()
    {
        if (ActivePlan is null) return 0;

        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var pass in ActivePlan.ScheduledPasses)
        {
            if (now > pass.Scored.Pass.LosUtc)
                count++;
        }
        return count;
    }

    private void RebuildScheduledPasses()
    {
        ScheduledPasses.Clear();

        if (ActivePlan is null)
            return;

        var passes = ActivePlan.ScheduledPasses;
        for (var i = 0; i < passes.Count; i++)
        {
            var scheduled = passes[i];
            var pass = scheduled.Scored.Pass;

            var gapBefore = "";
            var isTightGap = false;
            if (i > 0)
            {
                var previousLos = passes[i - 1].Scored.Pass.LosUtc;
                var gap = pass.AosUtc - previousLos;
                gapBefore = FormatDuration(gap);
                isTightGap = gap < TimeSpan.FromMinutes(2);
            }

            ScheduledPasses.Add(new SessionPlanPassRow
            {
                Source = scheduled,
                SatelliteName = pass.SatelliteName,
                AosDisplay = FormatTime(pass.AosUtc),
                LosDisplay = FormatTime(pass.LosUtc),
                MaxElevation = $"{pass.MaxElevationDeg:F1}°",
                CompositeScore = $"{scheduled.Scored.CompositeScore:F1}",
                Duration = FormatDuration(pass.Duration),
                GapBefore = gapBefore,
                IsTightGap = isTightGap,
                IsForceIncluded = scheduled.Reason == PassSelectionReason.ForceIncluded,
                Reason = scheduled.Reason.ToString()
            });
        }
    }

    private string FormatTime(DateTime utcTime)
    {
        var displayTime = UseUtcTime ? utcTime : utcTime.ToLocalTime();
        var format = Use24HourClock ? "HH:mm:ss" : "h:mm:ss tt";
        return displayTime.ToString(format);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}

/// <summary>
/// Display row for the session planner timeline.
/// </summary>
public sealed class SessionPlanPassRow
{
    public required ScheduledPass Source { get; init; }
    public string SatelliteName { get; init; } = "";
    public string AosDisplay { get; init; } = "";
    public string LosDisplay { get; init; } = "";
    public string MaxElevation { get; init; } = "";
    public string CompositeScore { get; init; } = "";
    public string Duration { get; init; } = "";
    public string GapBefore { get; init; } = "";
    public bool IsTightGap { get; init; }
    public bool IsForceIncluded { get; init; }
    public string Reason { get; init; } = "";
}
