# JMScheduler — Complete Business Rules

**For:** QA Testers  
**Version:** 1.0  
**Date:** 2026-02-08  

This document lists every business rule the JMScheduler job must follow. Each rule has a unique ID so testers can reference them in test cases. Rules are grouped by phase of the job.

---

## PHASE 0: Job Startup & Concurrency

| ID | Rule | How to Verify |
|----|------|---------------|
| START-1 | Only one instance of the job may run at a time. If a second instance starts while the first is running, it must exit immediately without processing anything. | Start two instances simultaneously. Second one logs "StartEvent returned 0" and exits. DB has only one run entry. |
| START-2 | Every run creates a row in `job_scheduler_run` with a unique `RunId`, `StartedAt` timestamp, and initial status. | Query `job_scheduler_run` after starting the job. New row should exist immediately. |
| START-3 | Every run logs to `logs.jobtracking` at start, after cleanup, after weekly, after monthly, and on completion or failure. | Query `logs.jobtracking` after a run. At least 4-5 entries with the same session context. |

---

## PHASE 1: Cleanup — Deletion Rules

These rules govern which shifts the job is allowed to delete.

| ID | Rule | How to Verify |
|----|------|---------------|
| CLN-1 | **Only shifts dated TOMORROW or later can ever be deleted.** Shifts for today (`fromdate = CURDATE()`) and any past date are never touched by cleanup, regardless of model status. | Delete or reset a model. Confirm today's and past shifts for that model survive the run. Only tomorrow+ shifts are removed. |
| CLN-2 | **A shift with a linked timecard (`Employeetimecard_id > 0`) is never deleted.** Even if its model is deleted, reset, or deactivated. | Create a shift, clock in (creates TC link). Delete the model. Run job. Shift with TC remains. |
| CLN-3 | **A shift with a claim (`employeescheduleshiftclaim`) is never deleted.** Even if its model is deleted, reset, or deactivated. | Create a shift with a claim. Delete the model. Run job. Shift with claim remains. |
| CLN-4 | If all three conditions are met — (a) shift is tomorrow or later, (b) no timecard linked, (c) no claim — then the shift IS deleted when its model is orphaned (deleted), reset (`IsModelReset = 1`), or deactivated (`IsActive = 0`). | Delete a model. Future unclaimed, un-timecarded shifts disappear. |
| CLN-5 | After cleanup runs, all models with `IsModelReset = 1` are set to `IsModelReset = 0` and `lastrundate = yesterday`. | Query the model after a run. `IsModelReset` should be 0. |
| CLN-6 | Multi-week models that were edited (IsModelReset = 1) have their tracking anchor reset, causing shifts to regenerate from today forward. | Edit a biweekly model. Run job. New shifts start from today's cycle, not the old anchor. |

---

## PHASE 2: Model Loading — Which Models Are Processed

### Weekly Models (RecurringType = 0)

| ID | Rule | How to Verify |
|----|------|---------------|
| LOAD-W1 | Only models with `IsActive = 1` are loaded. Inactive models are ignored. | Deactivate a model. Run job. No shifts created for it. |
| LOAD-W2 | Only models with `RecurringType = 0` are treated as weekly. | Confirm monthly models (RecurringType = 1) are not processed by the weekly service. |
| LOAD-W3 | Models with an end date in the past (`enddate < today`) are not loaded. | Set `enddate` to yesterday. Run job. No shifts created. |
| LOAD-W4 | Models with `noenddate = true` (sentinel enddate = `0001-01-01`) are always loaded regardless of enddate value. | Create a model with no end date. Run job. Shifts created for all advance days. |
| LOAD-W5 | Models whose `lastrundate` is today or later are skipped (already ran today). | Run job twice. Second run creates zero shifts but still updates lastrundate. |
| LOAD-W6 | The model's company must have `AccountStatus = 'Active'`. Models belonging to inactive companies are not loaded. | Deactivate the company. Run job. No shifts for any models of that company. |

### Monthly Models (RecurringType = 1)

| ID | Rule | How to Verify |
|----|------|---------------|
| LOAD-M1 | Only models with `RecurringType = 1` are treated as monthly. | Confirm weekly models are not processed by the monthly service. |
| LOAD-M2 | Today's date must be >= the model's `startdate`. Future-start models are not processed for months before their start. | Set `startdate` to next month. Run job. No shift for current month. |
| LOAD-M3 | Models are loaded if their `lastrundate` is on or before the last day of the target month. This allows re-runs and multi-month catch-up. | Set lastrundate to last month. Run job. Model creates shifts for current month. |

---

## PHASE 3: Shift Creation — Weekly

| ID | Rule | How to Verify |
|----|------|---------------|
| WEEK-1 | Shifts are created for each day from today through today + `AdvanceDays` (default 90). | Run job. Count shifts. Should cover ~90 days of matching weekdays. |
| WEEK-2 | Only days matching the model's day-of-week flags are created. If model has Mon/Wed/Fri set, no Tue/Thu/Sat/Sun shifts. | Create a Mon-only model. Run job. Only Monday shifts appear. |
| WEEK-3 | The model's `startdate` must be <= today. A model starting next week does not create shifts until that date arrives. | Set `startdate` = next week. Run job. No shifts yet. Change to yesterday. Run job. Shifts appear. |
| WEEK-4 | If the model has an `enddate`, no shifts are created beyond that date. | Set `enddate` = 2 weeks from now. Run job. Shifts only go up to that date. |
| WEEK-5 | **Duplicate prevention:** A shift with the same `(ClientId, EmployeeId, DateTimeIn, DateTimeOut)` is never created twice, even across multiple job runs. | Run job twice. Second run: 0 new shifts, all marked "Duplicate" in audit log. |
| WEEK-6 | **ScheduleType = 1 (Open With All Claim):** Duplicate check uses `(ModalId, ClientId, EmployeeId, DateTimeIn, DateTimeOut)`. This allows DIFFERENT models to create shifts for the same time slot, but the SAME model will not duplicate on re-run. | Create two ScheduleType=1 models for same slot. Run job. Both create shifts. Run again. Zero new shifts. |
| WEEK-7 | **Overlap blocking:** If Employee X already has a shift at Location A from 9-5, a new shift for Employee X at Location B from 8-4 is blocked. Logged in `job_shift_conflicts`. | Create overlapping shifts at two different locations for the same employee. Second one is blocked. |
| WEEK-8 | **Same-location overlap is NOT blocked** — it's handled by duplicate prevention (WEEK-5). If two models create overlapping times at the same location, dedup catches it. | Two models, same client, overlapping times. Only one shift created (dedup). No conflict logged. |
| WEEK-9 | **EmployeeId = 0 (unassigned) skips overlap check.** Open/unassigned shifts never conflict. | Create unassigned shift overlapping an assigned shift at a different location. Both are created. |
| WEEK-10 | **Note = "Scheduled Event"** on every weekly shift. | Check `Note` column on new shifts. |
| WEEK-11 | **Claims are copied.** If the model has rows in `employeescheduleshiftmodelclaim`, they are copied to `employeescheduleshiftclaim` for each new shift. | Model with 3 claims. Run job. Each new shift has 3 matching claim rows. |
| WEEK-12 | **Group schedule is cloned.** If the model has a `GroupScheduleId > 0`, the existing `groupschedule` row is cloned (same Client_id, flags). The new shift uses the cloned group ID. | Model with GroupScheduleId. Run job. New shift's GroupScheduleId points to a NEW `groupschedule` row with the same data as the original. |
| WEEK-13 | **Group dedup:** When multiple models share the same `GroupScheduleId`, the group is processed once (one clone, one set of shifts). Not N times for N models. | 3 models in one group. Run job. Only 1 group clone, not 3. |
| WEEK-14 | **Scan areas are copied** for models that have scan area templates. Calls `ProcessRecurring_ScheduleScanArea` stored proc per shift. | Model with scan area template. Run job. New shift has matching scan area records. |
| WEEK-15 | **Overnight/multi-day shifts** are supported via `DaySpan`. A model with FromDate at 11 PM and ToDate at 6 AM (next day) correctly creates shifts spanning midnight. | Create overnight model (11 PM — 6 AM). Run job. Shift's `datetimeout` is on the next calendar day. |

---

## PHASE 4: Shift Creation — Monthly

| ID | Rule | How to Verify |
|----|------|---------------|
| MONTH-1 | Shifts are created for each of the next `MonthlyMonthsAhead` months (default 12), starting from the current month. | Run job. Verify shifts exist for the correct weekday occurrence in each of the next 12 months. |
| MONTH-2 | The target date is the Nth occurrence of the specified weekday in the month. `MonthlyRecurringType`: 0 = 1st, 1 = 2nd, 2 = 3rd, 3 = 4th. | Model = "2nd Tuesday". Verify each shift falls on the 2nd Tuesday of its month. |
| MONTH-3 | **4th occurrence overflow protection:** If the month doesn't have a 5th occurrence, requesting the "4th" falls back to the LAST occurrence. E.g., "4th Monday" in a month with only 4 Mondays gives the 4th. In a month where adding 7 days would spill into next month, it backs up. | Create a "4th Friday" model for a month that has only 4 Fridays. Verify correct date. |
| MONTH-4 | **Target date must be >= model's startdate.** If the model starts on March 15 and the 1st Monday of March is March 3, no shift for March. | Model starts mid-month. Run job. Confirm that month is skipped if the target date precedes startdate. |
| MONTH-5 | **Duplicate prevention** works the same as weekly (WEEK-5, WEEK-6). | Run job twice. No duplicate monthly shifts. |
| MONTH-6 | **Overlap blocking** works the same as weekly (WEEK-7, WEEK-8, WEEK-9). | Same tests as weekly overlap, applied to monthly shifts. |
| MONTH-7 | **Note = "Schedule Event Monthly"** (not "Scheduled Event"). | Check `Note` column. Must say "Schedule Event Monthly". |
| MONTH-8 | **Claims are NOT copied for monthly shifts.** Even if the model has claims. | Model with claims. Run job. Monthly shifts have zero claim rows. |
| MONTH-9 | **Group handling is different from weekly.** Monthly creates a NEW `groupschedule` row with `IsEmployeeSchedule = 1`, `IsClientSchedule = 0`. It does NOT clone the existing group row. | Model with GroupScheduleId. Run job. Check the new groupschedule row. `IsEmployeeSchedule = 1`. |
| MONTH-10 | **Scan areas** are copied for monthly shifts (same as weekly). | Model with scan area template. Run job. Monthly shift has scan area records. |

---

## PHASE 5: Multi-Week (Biweekly, Triweekly, etc.)

| ID | Rule | How to Verify |
|----|------|---------------|
| MWEEK-1 | A model with `RecurringOn = 2` creates shifts every 2 weeks (biweekly). `RecurringOn = 3` = every 3 weeks. | Biweekly model. Run job. Shifts only appear in alternating weeks. |
| MWEEK-2 | The week cycle is anchored to the model's first run date or the tracking table's `Nextscheduledate`. Changing the anchor changes which weeks are "on". | Check `job_ClientscheduleShiftnextrunStatus` for the model. Verify the shifts align with the anchor. |
| MWEEK-3 | When a multi-week model is edited (IsModelReset = 1), the anchor is reset to the last historical schedule date, and shifts regenerate from today. | Edit a biweekly model. Run job. Shifts recalculate from today's cycle. |
| MWEEK-4 | Multi-week models that have never been run use `startdate` as the anchor. | Create a brand-new biweekly model. Run job. Cycle starts from its startdate. |
| MWEEK-5 | All standard weekly rules (WEEK-1 through WEEK-15) also apply to multi-week models — they are a subset of weekly processing with an additional week-skipping filter. | Run full test suite on a biweekly model. All dedup, overlap, claims, group, scan area rules apply. |

---

## PHASE 6: Finalization — lastrundate

| ID | Rule | How to Verify |
|----|------|---------------|
| FINAL-1 | **Weekly lastrundate = NOW()** for ALL loaded weekly models, even those that created zero new shifts (all duplicates). | Run job. Model with 100% duplicates still has `lastrundate = today`. |
| FINAL-2 | **Monthly lastrundate = 1st of next month** for ALL loaded monthly models, per month processed. | Run job. Monthly model's lastrundate = first day of next month (not NOW). |
| FINAL-3 | Multi-week tracking (`job_ClientscheduleShiftnextrunStatus`) is updated with the latest generated shift date for models that created new shifts. | Check tracking table after run. `Nextscheduledate` reflects the most recent generated date. |
| FINAL-4 | Audit tables (`job_shift_audit_log`, `job_shift_conflicts`, `job_scheduler_run`) are pruned to `AuditRetentionDays` (default 3 days). | Check old audit rows. Entries older than 3 days are deleted. |

---

## PHASE 7: Field Propagation — Model to Shift

Every field on the new shift must match the source model. Verify these specific fields:

| ID | Shift Field | Source | Special Handling |
|----|------------|--------|-----------------|
| FLD-1 | `ModalId` | `model.Id` | Always set — links shift back to its model. |
| FLD-2 | `employeeid` | `model.EmployeeId` | Can be 0 (unassigned). |
| FLD-3 | `Client_id` | `model.Client_id` | — |
| FLD-4 | `CompanyID` | `model.CompanyID` | — |
| FLD-5 | `datetimein` | Calculated: `scheduleDate + model.FromDate.TimeOfDay` | For overnight shifts, stays on the target date. |
| FLD-6 | `datetimeout` | Calculated: `scheduleDate + model.ToDate.TimeOfDay` (or +DaySpan for overnight) | For overnight, ends on the next calendar day. |
| FLD-7 | `fromdate` | `datetimein.Date` | Date-only portion of scheduled start. |
| FLD-8 | `todate` | `datetimeout.Date` | Date-only portion of scheduled end. |
| FLD-9 | `Duration` | `model.Duration` | Decimal hours. |
| FLD-10 | `Note` | `"Scheduled Event"` (weekly) or `"Schedule Event Monthly"` (monthly) | Hard-coded per type. |
| FLD-11 | `ScheduleType` | `model.ScheduleType` | 0=Individual, 1=OpenWithAllClaim, 2=Claim, 3=Team. Can be NULL if model has NULL. |
| FLD-12 | `IsTeamSchedule` | `model.IsTeamSchedule` | Boolean. |
| FLD-13 | `GroupScheduleId` | Cloned ID (weekly) or new ID (monthly) | See WEEK-12 and MONTH-9. |
| FLD-14 | `IsActive` | Always `1` (true) | New shifts are always active. |
| FLD-15 | `Employeetimecard_id` | Always `NULL` | Never set during creation. Only set when employee clocks in. |
| FLD-16 | `actualtimein` | Always `NULL` | Only set when employee clocks in. |
| FLD-17 | `actualtimeout` | Always `NULL` | Only set when employee clocks out. |
| FLD-18 | `CreateUser_id` | Always `41` | System user ID for job-created shifts. |
| FLD-19 | `BreakDetailID` | `model.BreakDetailID` | **0 becomes NULL** via `NULLIF()` to prevent FK violation. |
| FLD-20 | `IsLateInAlert` | `model.IsLateInAlert` | Boolean — enables late-in alerting. |
| FLD-21 | `LateInDuration` | `model.LateInDuration` | Minutes threshold for late-in. |
| FLD-22 | `IsLateOutAlert` | `model.IsLateOutAlert` | Boolean — enables early-out alerting. |
| FLD-23 | `LateOutDuration` | `model.LateOutDuration` | Minutes threshold for early-out. |
| FLD-24 | `IsAutoClockOut` | `model.IsAutoClockOut` | Boolean. |
| FLD-25 | `AutoClockOutHour`, `AutoClockOutMinutes` | `model.AutoClockOutHour`, `model.AutoClockOutMinutes` | — |
| FLD-26 | `JobClassification_Id` | `model.JobClassification_Id` | — |
| FLD-27 | `IsRounding`, `RoundUp`, `RoundDown` | `model.*` | Time rounding configuration. |
| FLD-28 | `IsFlatRate`, `FlatRate` | `model.*` | Flat rate pay configuration. |
| FLD-29 | `IsPublished` | `model.IsPublished` | Whether shift is visible to employees. |
| FLD-30 | All schedule restriction fields | `model.*` | `IsScheduleClockInRestrictionEnable`, `ScheduleRestrictClockInBefore`, `ScheduleRestrictClockInAfter`, `ScheduleRestrictClockOutBefore`, `ScheduleRestrictClockOutAfter`, `ScheduleRestrictMinDuration`, `ScheduleRestrictMaxDuration`, `IsScheduleRestrictionEnable`, `IsScheduleDurationRestrictionEnable`. |
| FLD-31 | `IsSuppressedScheduleRestriction` | `model.IsSuppressedScheduleRestriction` | — |
| FLD-32 | `IsManagerApprovalEnabled` | `model.IsManagerApprovalEnabled` | — |
| FLD-33 | `ScheduleScanType` | `model.ScheduleScanType` | — |
| FLD-34 | `UserNote` | `model.UserNote` | Free-text note from the model. |

---

## PHASE 8: Audit & Observability

| ID | Rule | How to Verify |
|----|------|---------------|
| AUDIT-1 | Every shift attempt (created, duplicate, overlap, error) is logged in `job_shift_audit_log` with the RunId. | Query audit log after run. One row per model-per-day attempt. |
| AUDIT-2 | Every overlap conflict is logged in `job_shift_conflicts` with both the blocked shift and the conflicting shift details. | Trigger an overlap. Check `job_shift_conflicts` for both shift IDs, times, and locations. |
| AUDIT-3 | `job_scheduler_run` is updated on completion with: `CompletedAt`, `DurationSeconds`, `Status` (Completed/Failed/Cancelled), `ShiftsCreated`, `ShiftsSkipped`, `OrphanedDeleted`, `ResetDeleted`, `AuditEntriesCount`, `ConflictsCount`, `ErrorMessage` (if failed). | Query `job_scheduler_run` after run. All columns populated. |
| AUDIT-4 | On failure, `Status = 'Failed'` and `ErrorMessage` contains the exception message. The job re-throws the exception (non-zero exit code for Task Scheduler). | Introduce a DB error. Run job. Check run summary for "Failed" status and error text. |
| AUDIT-5 | On cancellation (Ctrl+C or service stop), `Status = 'Cancelled'`. | Cancel the job mid-run. Check run summary. |

---

## PHASE 9: Performance & Safety

| ID | Rule | How to Verify |
|----|------|---------------|
| PERF-1 | Inserts are batched (`InsertBatchSize`, default configurable). Not row-by-row. | Enable debug logging. Observe batch sizes in logs. |
| PERF-2 | Sleep between batches (`SleepBetweenBatchesMs`) yields to web/mobile/alert traffic. | Monitor DB connections during run. Gaps between batch inserts visible. |
| PERF-3 | Deadlocks are retried up to `MaxDeadlockRetries` times with exponential backoff. | Simulate concurrent DB load. If deadlock occurs, logs show retry attempts before success or failure. |
| PERF-4 | Duplicate detection and overlap detection are done in-memory (HashSets), not per-row DB queries. | Monitor DB query count. Only initial bulk loads, no per-shift COUNT queries. |

---

## Quick Reference: Key Differences Between Weekly and Monthly

| Aspect | Weekly | Monthly |
|--------|--------|---------|
| RecurringType | 0 | 1 |
| Advance window | `AdvanceDays` (90 days) | `MonthlyMonthsAhead` (12 months) |
| Note text | "Scheduled Event" | "Schedule Event Monthly" |
| Claims copied? | YES | NO |
| Group handling | CLONE existing group row | CREATE NEW group row |
| lastrundate | NOW() | 1st of next month |
| Target date | Every matching weekday | Nth weekday of month |

---

*End of Business Rules. For bug details and test case templates, see `SCHEDULER_BUG_TRACKER.md`.*
