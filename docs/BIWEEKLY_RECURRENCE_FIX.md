# Multiweek recurrence architecture and rolling-window fix

## Purpose

This document records how JMScheduler replaces the original MySQL scheduling
procedures, the root cause of the every-2+-weeks rollover defect, and how to
verify and deploy the narrowly scoped correction.

The fix applies only to weekly models whose `clientschedulemodel.recurringon`
value is greater than 1. Monthly models and ordinary weekly models are not
changed.

## System objective

The original MySQL procedures correctly generated recurring shifts but took
hours because they repeatedly queried and populated working tables while
processing models and dates row by row.

The .NET 8 implementation preserves the database behavior while moving the
expensive work into memory:

1. `JMScheduler.Api` exposes `POST /api/scheduler/run`.
2. `SchedulerJob` chooses the full batch path or the lean single-model path.
3. `ScheduleRepository` loads models, existing shift keys, claims, scan areas,
   groups, and multiweek tracking data in bulk.
4. `WeeklyScheduleService` iterates the configured rolling date window.
5. `MultiWeekDateCalculator` computes valid every-N-weeks dates in memory.
6. Existing-shift hash sets provide constant-time duplicate checks.
7. Shifts are grouped into bulk-insert and specialized slow paths.
8. Last-run and multiweek tracking state is finalized after successful inserts.

The default weekly horizon remains 45 days. A model with no end date does not
create infinite records at once; each scheduler run must move the 45-day window
forward.

## Original SQL to .NET mapping

- `schedular.sql` / `CallProcessScheduleModal` maps to `SchedulerJob` for
  orchestration, cleanup, model loading, and finalization.
- `processScheduleModel.sql` / `ProcessScheduleModal` maps to
  `WeeklyScheduleService` for weekly and multiweek model processing.
- `SpanClientScheduleShift` maps to `MultiWeekDateCalculator` for every-N-weeks
  date eligibility.
- `MonthlySchedular.sql` maps to `MonthlyScheduleService`.
- `ClientShiftModalEditable` maps to
  `CleanupService.ResetEditedModelAnchorsAsync`.
- `ProcessRecurring_ScheduleScanArea` remains a stored procedure called from a
  specialized repository insert path.

The original multiweek function used these working/state tables:

- `job_ClientscheduleShiftnextrunStatus`
- `job_clientscheduletempfunctiondata`
- `job_clientscheduletempfunctiondataweekly`
- `job_clientschedulefunctiondataHistory`

The C# service replaces the temporary-table date calculation with a
`HashSet<DateTime>`. Existing tracking/history rows remain useful for reset
handling and for finding the last generated shift, but they must not determine
the recurrence phase or the end of the current generation window.

## Multiweek invariants

For a model with `recurringon = N`:

1. The recurrence phase is permanently anchored to
   `clientschedulemodel.startdate`.
2. Cycle starts occur every `N * 7` days from that date.
3. Day flags select dates within each seven-day cycle.
4. The scheduler run date starts the rolling generation window.
5. `AdvanceDays` determines the inclusive end of that window.
6. The last active shift/history date is only a lower restriction used to avoid
   regenerating previously handled dates.
7. Database duplicate keys remain the final idempotency guard.

Mutable values such as `lastrundate`, `Nextscheduledate`, and the last generated
shift must never become the permanent cycle root.

## Reported reproduction

Model:

- Start date: Friday, June 19, 2026
- Days: Friday, Saturday, Sunday
- Interval: every 2 weeks
- End date: none
- Advance window: 45 days

The initial valid dates are:

- June 19-21
- July 3-5
- July 17-19
- July 31-August 2

When the rolling window advances beyond August 2, the next dates must remain in
the original phase:

- August 14-16
- August 28-30
- September 11-13

They must not shift into the alternate weeks, duplicate the initial dates, or
stop because an old tracking/history date did not advance.

## Root cause

Two related defects existed:

1. Before commit `dfac369`, the C# calculator used the latest mutable
   tracking/history date as the cycle root. If that date was Friday, Saturday,
   or Sunday inside a selected cycle, later runs could shift the entire
   biweekly phase.
2. Commit `dfac369` correctly restored `model.StartDate` as the immutable cycle
   root, but the calculator still ended its search at
   `anchorDate + AdvanceDays`. The anchor can remain an older history/tracking
   date when the current run creates no shift. Its endpoint can therefore stay
   behind the current 45-day horizon. Because no shift is created, tracking
   does not move, and later runs can remain stalled.

The second issue explains why the initial 45-day records exist but no later
records are automatically materialized.

## Implemented correction

`MultiWeekDateCalculator.CalculateValidDates` now receives the scheduler's
current window start and ends calculation at:

```text
windowStartDate + AdvanceDays
```

The cycle root remains `model.StartDate`. The restriction remains the last
generated shift/reset boundary.

Both callers now provide the actual run date:

- Batch: `WeeklyScheduleService` passes its `scheduleDateTime`.
- Single model: `SchedulerJob.RunSingleModelAsync` passes `now.Date`.

No database schema or stored-procedure change is required.

## Automated verification

The `JMScheduler.Core.Tests` project covers:

- The exact June 19, 2026 biweekly pattern.
- The next rolling window after the original 45-day horizon.
- Three-week and four-week cycle alignment.
- Repeated-run non-overlap and no phase drift.
- Exclusion of occurrences beyond the requested window.

Run:

```bash
dotnet test JMScheduler/JMScheduler.sln
dotnet build JMScheduler/JMScheduler.sln --configuration Release
```

## Deployment

1. Back up the currently deployed API files and record the deployed build.
2. Build and publish `JMScheduler.Api` from the reviewed commit.
3. Replace the IIS application files using the existing deployment procedure.
4. Recycle the application pool.
5. Call `GET /api/scheduler/status` and confirm a healthy response.
6. Run one known test model through `POST /api/scheduler/run`.
7. Poll status until completion and verify the expected future dates.
8. Let the regular batch trigger run once and check its completion/audit data.

The service runs API jobs in the background. A successful immediate HTTP
response means the job was queued, not that generation completed; poll the
status endpoint and inspect the final result.

## Production verification

For the affected model, verify:

1. Active shifts exist on the expected every-two-weeks dates.
2. No shifts were created in alternate weeks.
3. No duplicate `(Client_id, employeeid, datetimein, datetimeout)` records were
   introduced.
4. `clientschedulemodel.lastrundate` updated after processing.
5. The next regular scheduler run remains idempotent.
6. Monthly and `recurringon = 1` sample models are unchanged.

## Rollback

This correction changes only in-memory date-window selection and its two
callers. It does not migrate data.

To roll back:

1. Restore the previous published API files or redeploy the preceding commit.
2. Recycle the IIS application pool.
3. Do not delete generated shifts automatically.
4. Review shifts created by the corrected run before manually deactivating any
   records; preserve shifts linked to employee timecards.

If production is running a build older than `dfac369`, deploy this fix together
with that cycle-alignment correction. Deploying only the rolling endpoint change
on top of the older drifting-cycle implementation is not safe.
