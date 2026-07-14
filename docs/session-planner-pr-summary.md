# Session Planner — Feature Summary

## What it does

The Session Planner lets operators define a time window for an operating session and automatically generates an optimal schedule of satellite passes to work. It uses weighted interval scheduling to select a non-overlapping subset of passes that maximises a combined score of pass quality and satellite priority. During plan execution, the application automatically switches the focused satellite at each pass's AOS time and provides pre-alert notifications.

## How to access it

**Passes → Session Planner** in the menu bar. Opens as a modal window.

## UI Layout

- **Top bar**: CalendarDatePicker + TimePicker for start and end times, Generate Plan button, Reset button
- **Execution controls**: Start, Pause, Resume, Stop buttons with live current-pass display, countdown, and progress
- **Schedule table**: Satellite, AOS, LOS, Elevation, Score, Duration, Gap columns (auto-sized)
- **Bottom bar**: Export CSV, Export ICS, Save Plan, Load Plan buttons; Close button

## New files added

### OscarWatch.Core/SessionPlanner/
| File | Purpose |
|------|---------|
| `TransponderCategory.cs` | Enum: Linear, Fm, Mixed, Unknown |
| `ScoredPass.cs` | Candidate pass with quality/composite scores |
| `ScheduledPass.cs` | Selected pass + selection reason enum |
| `SessionPlan.cs` | Immutable plan with scheduled passes, candidates, exclusions |
| `PlanExecutionState.cs` | Enum: Idle, Running, Paused, Completed |
| `PlanExecutorAction.cs` | Discriminated union for tick results (NoAction, SwitchFocus, RaisePreAlert, MarkCompleted) |
| `PassQualityScorer.cs` | Pure static scoring: elevation×0.5 + duration×0.3 + transponder×0.2, composite formula |
| `WeightedIntervalScheduler.cs` | O(n log n) DP algorithm for optimal non-overlapping pass selection |
| `SessionPlannerService.cs` | Orchestration: predict → score → schedule, with validation and adjustment |
| `PlanExecutor.cs` | Timer-driven state machine for focus switching and pre-alerts |
| `SessionPlanPersistence.cs` | JSON serialise/deserialise with version validation |

### OscarWatch.Core/Export/
| File | Purpose |
|------|---------|
| `SessionPlanExporter.cs` | CSV and ICS (with VALARM pre-alerts) export |

### OscarWatch/ViewModels/
| File | Purpose |
|------|---------|
| `SessionPlannerViewModel.cs` | Full MVVM ViewModel with all commands, date/time picker bindings, execution tick |

### OscarWatch/Views/
| File | Purpose |
|------|---------|
| `SessionPlannerWindow.axaml` | Avalonia view with DataGrid, date/time pickers, export file dialogs |
| `SessionPlannerWindow.axaml.cs` | Code-behind for file dialog interactions |

## Modified files

| File | Change |
|------|--------|
| `OscarWatch.Core/Models/AppSettings.cs` | Added `SatellitePriorities` dictionary and `SessionPlannerPreAlertMinutes` |
| `OscarWatch.Core/Services/SettingsService.cs` | Null-coalescing for `SatellitePriorities` in `NormalizeSettings` |
| `OscarWatch.Core/Properties/AssemblyInfo.cs` | Added `InternalsVisibleTo("OscarWatch")` |
| `OscarWatch/App.axaml.cs` | DI registration for SessionPlannerService, PlanExecutor, SessionPlannerViewModel |
| `OscarWatch/ViewModels/MainViewModel.cs` | Added `OpenSessionPlannerAsync` command |
| `OscarWatch/MainWindow.axaml` | Added "Session Planner" menu item under Passes |
| `OscarWatch/Resources/Strings.resx` | Added `Menu.Passes.SessionPlanner` localisation key |

## Scoring algorithm

```
QualityScore = clamp(elevation/90) × 0.5
             + clamp(duration/15) × 0.3
             + transponderFactor × 0.2

CompositeScore = QualityScore × (11 − SatellitePriority)
```

- Transponder factors: Linear=1.0, Mixed=0.8, Unknown=0.7, FM=0.6
- Priority: 1 (highest) to 10 (lowest), default 5
- Score range: 0.0 to 10.0

## Scheduling algorithm

Weighted interval scheduling via dynamic programming:
1. Filter by minimum elevation threshold
2. Sort candidates by LOS time
3. Binary search for compatible predecessors (p[i])
4. DP recurrence: `dp[i] = max(dp[i-1], dp[p[i]] + weight[i])`
5. Backtrack to recover selected set
6. Tie-break overlapping equal-score passes by higher elevation (epsilon)

Handles forced inclusions by fixing them first, removing conflicts, then solving sub-problems in the gaps.

## Plan execution

- State machine: Idle → Running → Paused/Completed
- Tick-driven (1s interval from ViewModel)
- Focus switches at each pass's AOS (within 1 second)
- Pre-alerts at `max(previousLOS, AOS − L minutes)` where L is configurable (default 3, range 1–15)
- Manual override detection: pauses if operator changes focus externally
- Retains focused satellite during gaps

## Test coverage

22 new tests (18 FsCheck property-based + 4 xUnit unit tests):

| Test file | Properties |
|-----------|-----------|
| `SessionPlannerScoringPropertyTests.cs` | Score bounded [0,1]; composite formula |
| `WeightedIntervalSchedulerPropertyTests.cs` | Non-overlap; optimality (brute-force ≤8 passes); elevation filtering; tie-breaking; AOS-sorted output |
| `SessionPlanAdjustmentPropertyTests.cs` | Time accounting; exclusion respected; forced inclusion respected |
| `PlanExecutorPropertyTests.cs` | Focus state correctness; pre-alert timing |
| `SessionValidationPropertyTests.cs` | 15-minute rounding; invalid window rejection |
| `SessionPlanPersistencePropertyTests.cs` | Serialisation round-trip |
| `SessionPlanExportPropertyTests.cs` | ICS structure (VEVENT count, VALARM trigger); CSV completeness |
| `SessionPlannerViewModelTests.cs` | Pause/resume transitions; plan completion; review-only mode |

All 1358 tests pass (including the 22 new ones). Build succeeds with zero warnings.

## Settings additions

```json
{
  "satellitePriorities": { "ISS": 1, "SO-50": 3 },
  "sessionPlannerPreAlertMinutes": 3
}
```

## Export formats

- **CSV**: Header + data rows (SatelliteName, NoradId, AOS_UTC, LOS_UTC, MaxElevationDeg, CompositeScore, Status)
- **ICS**: VCALENDAR with one VEVENT per pass, each with VALARM trigger at configured lead time
- **JSON**: Full plan persistence with version, session bounds, passes, scores, exclusions

## Known limitations / future work

- No satellite priority UI yet (values set via settings JSON only)
- Voice announcements for pre-alerts are wired in the executor events but not connected to the UI speech service
- No visual distinction for "tight gaps" (< 2 minutes) beyond the gap value being visible
- The window doesn't have a live-updating timer during execution (needs DispatcherTimer wiring in the view)
- Localization keys only added for English; other language resource files need the Session Planner strings
