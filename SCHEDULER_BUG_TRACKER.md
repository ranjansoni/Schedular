# JMScheduler — Bug Tracker & Test Plan

**Purpose:** Complete inventory of bugs found, bugs fixed, bugs pending, business rules, and QA test cases. Written for junior developers and QA testers who may not have the full project history.

**Last updated:** 2026-02-08

---

## TABLE OF CONTENTS

1. [Bugs Already Fixed](#1-bugs-already-fixed-in-c-scheduler-job)
2. [Bugs Identified — Not Yet Fixed (Scheduler Job)](#2-bugs-identified--not-yet-fixed-scheduler-job)
3. [Bugs Identified — Not Yet Fixed (Core API / TimeKeeperServices)](#3-bugs-identified--not-yet-fixed-core-api)
4. [Bugs Identified — Not Yet Fixed (Stored Procedures / Portal)](#4-bugs-identified--not-yet-fixed-stored-procedures--portal)
5. [Business Rules Reference](#5-business-rules-reference)
6. [Test Cases for QA](#6-test-cases-for-qa)
7. [Data Repair Status](#7-data-repair-status)

---

## 1. Bugs Already Fixed in C# Scheduler Job

These bugs were fixed during the SQL-to-C# migration. **Do not revert these.**

### BUG-F1: Dedup Key Range — Overnight Shifts Missed

| Field | Value |
|-------|-------|
| **Severity** | High |
| **File** | `ScheduleRepository.cs` — `LoadExistingShiftKeysAsync` |
| **Was** | Query used `datetimeout <= @EndDate`, which excluded overnight/multi-day shifts from the dedup set |
| **Fix** | Changed to `datetimein <= @EndDate` — a shift that *starts* before the window end must be in the dedup set even if it *ends* after |
| **Impact if reverted** | Duplicate shifts for overnight models (e.g., 11 PM — 6 AM) |

### BUG-F2: Group Schedule Duplication

| Field | Value |
|-------|-------|
| **Severity** | High |
| **File** | `WeeklyScheduleService.cs` |
| **Was** | When N models shared the same `GroupScheduleId`, the group's shifts were inserted N times (once per model) |
| **Fix** | Process one representative per `GroupScheduleId`; other models in the group reuse the same group insert |
| **Impact if reverted** | N×duplicate shifts for team/group schedules |

### BUG-F3: lastrundate Only Updated for Models That Created Shifts

| Field | Value |
|-------|-------|
| **Severity** | High |
| **File** | `SchedulerJob.cs` lines 244-248, 271-274 |
| **Was** | Only models that actually created new shifts got their `lastrundate` updated |
| **Fix** | Update `lastrundate` for ALL loaded models (weekly and monthly), regardless of whether they created shifts |
| **Impact if reverted** | Models with all-duplicate days never advance their `lastrundate`, get re-loaded every run, cause progressively slower execution |

### BUG-F4: Monthly Model Load Window

| Field | Value |
|-------|-------|
| **Severity** | Medium |
| **File** | `ScheduleRepository.cs` |
| **Was** | Monthly models loaded with too-tight date filter, causing reset/multi-month runs to skip models |
| **Fix** | Load when `DATE(csm.lastrundate) <= LAST_DAY(@ScheduleDate)` |
| **Impact if reverted** | Monthly models missed during resets or catch-up runs |

### BUG-F5: BreakDetailID Foreign Key Violation

| Field | Value |
|-------|-------|
| **Severity** | Medium |
| **File** | `ScheduleRepository.cs` — INSERT statements |
| **Was** | `BreakDetailID = 0` was inserted directly, violating FK to `breakdetail` table |
| **Fix** | Use `NULLIF(@BreakDetailID, 0)` so 0 becomes NULL |
| **Impact if reverted** | FK errors on shifts for models with no break configured |

### BUG-F6: Deadlock-Prone Single-Row Operations

| Field | Value |
|-------|-------|
| **Severity** | High (production availability) |
| **File** | Original stored procedures (all replaced) |
| **Was** | Original SPs used row-by-row INSERT inside cursors with no batching, sleep, or retry — caused deadlocks with web/mobile/alert traffic |
| **Fix** | Batch inserts with configurable `InsertBatchSize`, `SleepBetweenBatchesMs`, and `MaxDeadlockRetries` with exponential backoff |
| **Impact if reverted** | Deadlocks during peak hours, API timeouts for mobile clock-in |

---

## 2. Bugs Identified — Not Yet Fixed (Scheduler Job)

### BUG-P1: CleanupService Deletes Shifts With Active Timecards + Deletes Today/Past Shifts (CRITICAL)

| Field | Value |
|-------|-------|
| **Severity** | P0 — Critical (Root cause of production data loss) |
| **File** | `CleanupService.cs` — `DeleteOrphanedShiftsAsync` and `DeleteResetInactiveShiftsAsync` |
| **Status** | **FIXED (2026-02-08)** |
| **Impact** | Was hard-deleting `clientscheduleshift` rows that had `Employeetimecard_id` linked, and was including today's shifts in the delete scope. This caused "records disappeared" and "NA on Timecard Report" in production. |

**What was fixed (two safety rules enforced):**

1. **Tomorrow onwards only:** Changed date filter from `css.todate > NOW()` / `css.fromdate > CURDATE()` to `css.fromdate >= DATE_ADD(CURDATE(), INTERVAL 1 DAY)` — today's and past shifts are never deleted.
2. **Timecard guard:** Added `AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)` to both queries — shifts with a linked timecard are never deleted.

**Current code (after fix):**

```sql
-- DeleteOrphanedShiftsAsync
WHERE  cm.Id IS NULL
  AND  css.ModalId > 0
  AND  css.fromdate >= DATE_ADD(CURDATE(), INTERVAL 1 DAY)
  AND  esc.ClientScheduleShiftID IS NULL
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)

-- DeleteResetInactiveShiftsAsync
WHERE  css.fromdate >= DATE_ADD(CURDATE(), INTERVAL 1 DAY)
  AND  esc.ClientScheduleShiftID IS NULL
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
```

### BUG-P2: Hard DELETE Instead of Soft-Delete

| Field | Value |
|-------|-------|
| **Severity** | P1 — High |
| **File** | `CleanupService.cs` line 168 |
| **Status** | **NOT FIXED — Recommended improvement** |
| **Impact** | Shifts are permanently deleted with `DELETE FROM`. If any downstream data (timecards, punches, alerts) references the shift by ID, those records become orphaned with no recovery path. |

**Current code:**

```sql
DELETE FROM clientscheduleshift WHERE Id IN @Ids
```

**Recommended fix:**

```sql
UPDATE clientscheduleshift SET IsActive = 0 WHERE Id IN @Ids
```

This preserves the row for audit/recovery. The application already uses `IsActive = 1` in all read queries, so soft-deleted shifts won't appear in reports.

### BUG-P3: No Isolation Between Cleanup Failure and Processing

| Field | Value |
|-------|-------|
| **Severity** | P2 — Medium |
| **File** | `SchedulerJob.cs` line 120 |
| **Status** | Not fixed |
| **Impact** | If cleanup throws an exception, the entire job fails (caught by outer try/catch at line 322). Weekly and monthly processing never runs. Models that need new shifts created for the day are skipped entirely. |

**Recommended fix:** Wrap the cleanup call in its own try/catch. Log the error but allow weekly/monthly processing to continue. The next day's run can retry cleanup.

```csharp
try
{
    (orphanedDeleted, resetDeleted) = await _cleanupService.RunAsync(ct);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Cleanup phase failed — continuing with processing");
    runError = $"Cleanup failed: {ex.Message}";
}
```

---

## 3. Bugs Identified — Not Yet Fixed (Core API)

These bugs exist in `TimeKeeperServices.cs` (Core API) and `Timekeeper.svc.cs` (V30 API, disabled). They are **pre-existing** — not introduced by the C# scheduler job — but they cause 60% of timecards to be orphaned (unlinked to any shift).

### BUG-API1: Untracked Entity Causes Silent Save Failure (CRITICAL)

| Field | Value |
|-------|-------|
| **Severity** | P0 — Critical (systemic data loss since V30) |
| **File (Core)** | `TimeKeeperServices.cs` line 586 (Automatic mode) and line 1100 (Manual mode) |
| **File (V30)** | `Timekeeper.svc.cs` line 648 (same logic) |
| **Status** | **NOT FIXED in either API** |

**The bug:** `clientscheduleshift csm` is initialized to `new clientscheduleshift()` instead of `null`. When no matching schedule is found by the stored procedure, `csm` remains as this phantom untracked entity. The subsequent `if (csm != null)` check always passes. Properties like `Employeetimecard_id`, `actualtimein`, etc. are set on this phantom object. `db.SaveChanges()` silently ignores it because Entity Framework doesn't track `new`'d objects.

**Result:** The timecard and punch are committed (lines 567-584), but the shift is never updated. The timecard becomes a permanent orphan.

**Fix (both files, both Automatic and Manual code paths):**

```csharp
// BEFORE (broken):
clientscheduleshift csm = new clientscheduleshift();

// AFTER (correct):
clientscheduleshift csm = null;
```

### BUG-API2: Exception Swallowing After Partial Commit

| Field | Value |
|-------|-------|
| **Severity** | P1 — High |
| **File (Core)** | `TimeKeeperServices.cs` — outer try/catch |
| **File (V30)** | `Timekeeper.svc.cs` line 1418-1422 |
| **Status** | **NOT FIXED in either API** |

**The bug:** The entire `EmployeeTimeKeeping` method is wrapped in a broad `catch (Exception ex)`. The timecard and punch are committed (via `db.SaveChanges()`) early in the method. If any exception occurs AFTER that commit but BEFORE the shift linkage `db.SaveChanges()`, the shift update is lost. The catch block returns a generic error message and does not log which specific operation failed.

**Fix:** Wrap the schedule-matching and shift-linking block in its own inner try/catch with specific error logging. The outer catch should remain for unexpected failures.

### BUG-API3: ScheduleThreshold = 0 Causes Unmatchable Exact-Time Comparison

| Field | Value |
|-------|-------|
| **Severity** | P1 — High |
| **File** | Stored procedure `API_GetMatchingScheduleForTimecard` |
| **Status** | **NOT FIXED** |

**The bug:** The stored procedure uses `p_schedulethreshhold IS NOT NULL` to switch between two matching strategies:
- **NULL threshold** → permissive date-range match (matches any shift on the same day for the employee/client)
- **Non-NULL threshold** → exact time comparison within ±threshold minutes

When threshold = 0 (explicitly set), it activates exact-time matching with a 0-minute window. This requires the punch time to equal the scheduled time to the minute — practically impossible.

**Fix:** Either treat `0` as `NULL` (permissive), or enforce minimum threshold of 15 minutes in the application code.

### BUG-API4: Manual Mode Missing ShiftId Check

| Field | Value |
|-------|-------|
| **Severity** | P2 — Medium |
| **File** | `TimeKeeperServices.cs` line 1107 |
| **Status** | **NOT FIXED** |

**The bug:** In Automatic mode (line 593), the code checks `if (model.ShiftId == 0)` before calling the stored procedure. If `ShiftId` is non-zero, it uses `FindAsync(model.ShiftId)` directly — a more reliable path. Manual mode skips this check and always calls the stored procedure.

### BUG-API5: Manual Mode Missing KioskID Job Classification Logic

| Field | Value |
|-------|-------|
| **Severity** | P2 — Medium |
| **File** | `TimeKeeperServices.cs` — compare lines 641-654 (Automatic) vs. 1147-1152 (Manual) |
| **Status** | **NOT FIXED** |

**The bug:** Automatic mode has a KioskID-dependent branch for assigning `JobClassification_id` from the shift to the timecard. Manual mode always uses the non-kiosk branch. This may cause inconsistent job classification on timecards created via kiosk in Manual mode.

### BUG-API6: Portal Uses Completely Different Matching Logic

| Field | Value |
|-------|-------|
| **Severity** | P2 — Medium (behavioral inconsistency) |
| **File** | `ClockController.cs` line 1592 |
| **Status** | Known — not a bug per se, but a divergence |

The web portal's clock-in uses a direct LINQ query (match by employee, client, date, `actualtimein == null`) instead of the `API_GetMatchingScheduleForTimecard` stored procedure. This means the same clock-in event may match different shifts depending on whether the employee uses the mobile app vs. the web portal.

---

## 4. Bugs Identified — Not Yet Fixed (Stored Procedures / Portal)

### BUG-SP1: ScheduleType NULL Displayed as "Team Scheduled"

| Field | Value |
|-------|-------|
| **Severity** | P2 — Medium (UI display issue) |
| **File** | Stored procedure `JM_GetClientSchdeulShiftRecords` |
| **Status** | **NOT FIXED** |

**The bug:** The SP has a `CASE WHEN` for `ScheduleType` that falls through to `ELSE 'Team Scheduled'` when the value is NULL. This makes individual shifts with NULL ScheduleType appear as "Team Scheduled" on the Schedule Maintenance report.

**Fix options:**
- A: Modify the SP to return "Individual" for NULL: `WHEN ScheduleType IS NULL OR ScheduleType = 0 THEN 'Individual'`
- B: Backfill NULL ScheduleType values to 0 in `clientscheduleshift`
- C: Both A and B (recommended)

---

## 5. Business Rules Reference

These are the rules that the scheduler job must obey. Testers should verify each one.

### 5.1 Shift Creation — Weekly Models (RecurringType = 0)

| Rule ID | Rule | How It Works |
|---------|------|-------------|
| BR-W1 | Shifts are created for the next N days | `AdvanceDays` config (default 90). One pass per day from today to today+90. |
| BR-W2 | Day-of-week filtering | Model has flags: `sunday`, `monday`, ..., `saturday`. Only create shifts on matching days. |
| BR-W3 | Multi-week models skip weeks | If `RecurringOn > 1`, the model runs every N weeks. `MultiWeekDateCalculator` determines if the current date falls in an active week. |
| BR-W4 | Duplicate prevention | A shift with the same `(ClientId, EmployeeId, DateTimeIn, DateTimeOut)` is never created twice. Uses in-memory HashSet. |
| BR-W5 | ScheduleType = 1 allows multiple models for same slot | For "Open With All Claim" shifts, duplicate detection uses `(ModalId, ClientId, EmployeeId, DateTimeIn, DateTimeOut)` instead — different models can create shifts for the same time. Same model cannot. |
| BR-W6 | EmployeeId = 0 skips overlap check | Unassigned shifts cannot have employee-level time conflicts. |
| BR-W7 | Overlap = same employee, different location, overlapping time | If Employee X has a shift at Location A from 9-5 and you try to create one at Location B from 8-4, it's blocked and logged. Same-location overlap is handled by dedup (BR-W4). |
| BR-W8 | Note = "Scheduled Event" | Every weekly shift gets this note text. |
| BR-W9 | Claims are copied | `employeescheduleshiftmodelclaim` rows for the model are copied to `employeescheduleshiftclaim` for the new shift. |
| BR-W10 | Group schedule is cloned | If the model has a `GroupScheduleId`, the existing `groupschedule` row is cloned for the new shift. |
| BR-W11 | lastrundate = NOW() for all loaded models | Even if no shifts were created (all duplicates), the model's lastrundate is advanced. |

### 5.2 Shift Creation — Monthly Models (RecurringType = 1)

| Rule ID | Rule | How It Works |
|---------|------|-------------|
| BR-M1 | Shifts created for the Nth weekday of the month | E.g., "2nd Tuesday" — calculated per month. |
| BR-M2 | N months ahead | `MonthlyMonthsAhead` config (default 12). |
| BR-M3 | Same duplicate/overlap rules as weekly | BR-W4 through BR-W7 apply. |
| BR-M4 | Note = "Schedule Event Monthly" | Different note text from weekly. |
| BR-M5 | No claims copied | Monthly models do not copy claims (unlike weekly). |
| BR-M6 | New groupschedule row | Monthly creates a new `groupschedule` row (IsEmployeeSchedule=1, IsClientSchedule=0) instead of cloning. |
| BR-M7 | lastrundate = 1st of next month | For each month processed, the model's lastrundate is set to the 1st of the following month. |

### 5.3 Cleanup Rules

| Rule ID | Rule | How It Works |
|---------|------|-------------|
| BR-C1 | Orphaned shifts are deleted — future only | If a model is deleted but its shifts remain (`ModalId` → no matching model), delete shifts from **tomorrow onwards** that have no claims AND no timecard linked. Today's and past shifts are never touched. |
| BR-C2 | Reset/inactive model shifts are deleted — future only | If `IsModelReset = 1` or `IsActive = 0`, delete shifts from **tomorrow onwards** that have no claims AND no timecard linked. Today's and past shifts are never touched. |
| BR-C3 | **NEVER delete a shift with a timecard** | If `Employeetimecard_id IS NOT NULL AND Employeetimecard_id > 0`, the shift must be preserved regardless of model status. **(Fixed 2026-02-08)** |
| BR-C3b | **NEVER delete today's or past shifts** | Shifts with `fromdate <= CURDATE()` are never eligible for deletion. Only `fromdate >= tomorrow` can be deleted. **(Fixed 2026-02-08)** |
| BR-C4 | Multi-week model anchors reset on edit | When a multi-week model is edited (IsModelReset=1), its tracking anchor is reset so shifts regenerate from today. |
| BR-C5 | IsModelReset cleared after processing | All models with `IsModelReset = 1` are set to `IsModelReset = 0` after cleanup, and `lastrundate = yesterday`. |

### 5.4 Field Propagation Rules

| Rule ID | Rule |
|---------|------|
| BR-FP1 | `ScheduleType` on shift = `ScheduleType` on model. If model has NULL, shift gets NULL. |
| BR-FP2 | `IsTeamSchedule` on shift = `IsTeamSchedule` on model. |
| BR-FP3 | `GroupScheduleId` on shift = cloned group ID (weekly) or new group ID (monthly). |
| BR-FP4 | `CompanyID`, `Client_id`, `employeeid` — always from the model. |
| BR-FP5 | `datetimein`, `datetimeout` — calculated from model's time fields + the target date. |
| BR-FP6 | `Employeetimecard_id` = NULL on creation. Only populated when an employee clocks in. |
| BR-FP7 | All alert/restriction/rounding/flat-rate flags — copied from the model. |

---

## 6. Test Cases for QA

### TC-GROUP 1: Shift Creation — Weekly

| TC ID | Test Case | Steps | Expected Result | Business Rule |
|-------|-----------|-------|-----------------|---------------|
| TC-W01 | Basic weekly shift creation | Create a model: Mon-Fri, 9 AM - 5 PM, Client A, Employee X. Run job. | Shifts created for each weekday from today to today+90 days. No Sat/Sun shifts. | BR-W1, BR-W2 |
| TC-W02 | Duplicate prevention on re-run | Run the job a second time (same day, no model changes). | Zero new shifts created. `job_scheduler_run.ShiftsSkipped` equals the expected duplicate count. | BR-W4 |
| TC-W03 | ScheduleType=1 (Open With All Claim) | Create two models for the same Client A, EmployeeId=0, same time, ScheduleType=1. Run job. | Both models create shifts. Re-running creates zero new shifts. | BR-W5 |
| TC-W04 | Multi-week skip | Create a model with `RecurringOn=2` (every 2 weeks). Run job. | Shifts only appear in alternating weeks, starting from the model's anchor date. | BR-W3 |
| TC-W05 | Overlap blocking (different location) | Employee X has a shift at Client A, 9-5. Create model for Employee X at Client B, 8-4. Run job. | Client B shift is blocked. Entry in `job_shift_conflicts`. Audit log shows "Overlap". | BR-W7 |
| TC-W06 | Overlap NOT blocked (same location) | Employee X has a shift at Client A, 9-5 on Monday. Create another model for Employee X at Client A, 10-6 on Monday. Run job. | Duplicate detection handles this — second shift is not created (same composite key). No overlap conflict logged. | BR-W4, BR-W7 |
| TC-W07 | Unassigned shift (EmployeeId=0) | Create a model with EmployeeId=0. Ensure another model for a different employee overlaps the same time/different client. Run job. | Unassigned shift created without overlap check. The other employee's shift is also created. | BR-W6 |
| TC-W08 | Group schedule cloning | Create a weekly model with `GroupScheduleId` pointing to an existing `groupschedule` row. Run job. | New shifts have a new `GroupScheduleId` that points to a *cloned* row in `groupschedule` (same data, new ID). | BR-W10 |
| TC-W09 | Claims copied | Create a model with 2 claim rows in `employeescheduleshiftmodelclaim`. Run job. | Each new shift has 2 matching rows in `employeescheduleshiftclaim`. | BR-W9 |
| TC-W10 | Note text | Run job for weekly model. | All created shifts have `Note = 'Scheduled Event'`. | BR-W8 |
| TC-W11 | lastrundate advances | Run job. Check `lastrundate` on a model that had all duplicates (no new shifts). | `lastrundate` is updated to NOW(), not left at its old value. | BR-W11 |

### TC-GROUP 2: Shift Creation — Monthly

| TC ID | Test Case | Steps | Expected Result | Business Rule |
|-------|-----------|-------|-----------------|---------------|
| TC-M01 | Basic monthly shift creation | Create a monthly model: "2nd Tuesday, 8 AM - 4 PM". Run job. | Shifts created for the 2nd Tuesday of each of the next 12 months. | BR-M1, BR-M2 |
| TC-M02 | Monthly duplicate prevention | Run job twice. | Second run creates zero new monthly shifts. | BR-M3 |
| TC-M03 | Monthly note text | Check created shifts. | Note = "Schedule Event Monthly". | BR-M4 |
| TC-M04 | Monthly no claims | Model has claims. Run job. | No `employeescheduleshiftclaim` rows created for monthly shifts. | BR-M5 |
| TC-M05 | Monthly lastrundate | Check `lastrundate` after run. | Set to 1st of next month, not NOW(). | BR-M7 |
| TC-M06 | Monthly group handling | Model has `GroupScheduleId`. Run job. | New `groupschedule` row created (not cloned) with `IsEmployeeSchedule=1, IsClientSchedule=0`. | BR-M6 |

### TC-GROUP 3: Cleanup — CRITICAL PATH

| TC ID | Test Case | Steps | Expected Result | Business Rule |
|-------|-----------|-------|-----------------|---------------|
| TC-C01 | Orphaned shift deletion (future, no TC) | Delete a model from `clientschedulemodel`. Run job. | Shifts from tomorrow onwards (no claims, no TC) are deleted. Today's and past shifts are untouched. | BR-C1, BR-C3b |
| TC-C02 | **Orphaned shift with timecard NOT deleted** | Delete a model. One of its future shifts has `Employeetimecard_id = 12345`. Run job. | That specific shift is NOT deleted. All other orphaned shifts (no TC, future) are deleted. | BR-C3 |
| TC-C03 | **Reset model with active timecard NOT deleted** | Set `IsModelReset = 1` on a model. One future shift has an active timecard. Run job. | The shift with the TC is preserved. Other future shifts are deleted and regenerated. | BR-C3 |
| TC-C04 | Inactive model cleanup | Set `IsActive = 0` on a model. Run job. | Future shifts (tomorrow+) for that model (no claims, no TC) are deleted. | BR-C2, BR-C3b |
| TC-C05 | Claimed shift preservation | Delete a model. One shift has a row in `employeescheduleshiftclaim`. Run job. | That shift is NOT deleted (claim protects it). | BR-C1 |
| TC-C06 | IsModelReset cleared | Set `IsModelReset = 1` on a model. Run job. | After job completes: `IsModelReset = 0`, `lastrundate = yesterday`. | BR-C5 |
| TC-C07 | Multi-week anchor reset | Edit a multi-week model (triggers `IsModelReset = 1`). Run job. | `job_ClientscheduleShiftnextrunStatus.Nextscheduledate` is reset to the last historical date. Future shifts regenerate from today. | BR-C4 |
| TC-C08 | **Today's shift never deleted (orphaned model)** | Delete a model. Verify today's shift for that model exists with no TC. Run job. | Today's shift survives. Only tomorrow+ shifts are deleted. | BR-C3b |
| TC-C09 | **Today's shift never deleted (reset model)** | Set `IsModelReset = 1`. Verify today's shift exists with no TC. Run job. | Today's shift survives. Only tomorrow+ shifts are deleted. | BR-C3b |
| TC-C10 | **Past shift never deleted** | Delete a model. Verify past-dated shifts exist for that model. Run job. | All past-dated shifts remain untouched regardless of claims or TC status. | BR-C3b |

### TC-GROUP 4: Field Propagation

| TC ID | Test Case | Steps | Expected Result | Business Rule |
|-------|-----------|-------|-----------------|---------------|
| TC-FP01 | ScheduleType propagation | Create models with ScheduleType = 0, 1, 2, 3, and NULL. Run job. | Each shift's ScheduleType matches its model. NULL model → NULL shift. | BR-FP1 |
| TC-FP02 | IsTeamSchedule propagation | Create model with `IsTeamSchedule = 1`. Run job. | Shift has `IsTeamSchedule = 1`. | BR-FP2 |
| TC-FP03 | Employeetimecard_id is NULL on creation | Run job. Check all new shifts. | `Employeetimecard_id` is NULL for every new shift. | BR-FP6 |
| TC-FP04 | BreakDetailID = 0 becomes NULL | Create model with `BreakDetailID = 0`. Run job. | Shift has `BreakDetailID = NULL`, not 0 (no FK violation). | BUG-F5 |
| TC-FP05 | Alert/restriction flags | Create model with all alert flags set. Run job. | Shift has identical flag values. | BR-FP7 |

### TC-GROUP 5: Error Handling and Resilience

| TC ID | Test Case | Steps | Expected Result | Business Rule |
|-------|-----------|-------|-----------------|---------------|
| TC-E01 | Concurrent run prevention | Start two instances of the job simultaneously. | Second instance exits immediately with "StartEvent returned 0" log message. Only one instance runs. | Concurrency guard |
| TC-E02 | Job failure recorded | Introduce a temporary DB error (e.g., revoke permissions). Run job. | `job_scheduler_run.Status = 'Failed'`, `ErrorMessage` populated. `logs.jobtracking` has failure entry. | Error handling |
| TC-E03 | Audit table retention | Set `AuditRetentionDays = 0`. Run job. | Old entries in `job_shift_audit_log`, `job_shift_conflicts`, and `job_scheduler_run` are purged. New run's entries remain. | Retention |
| TC-E04 | Batch processing with sleep | Set `InsertBatchSize = 10`, `SleepBetweenBatchesMs = 100`. Run with many models. | Logs show batched inserts with pauses. No deadlocks. | Batching |

### TC-GROUP 6: Report Validation (Post-Run)

| TC ID | Test Case | Steps | Expected Result |
|-------|-----------|-------|-----------------|
| TC-R01 | Schedule Maintenance report | After job run, open Schedule Maintenance in portal. Filter by a client/date range. | All shifts visible with correct Type column (not "Team Scheduled" for individual shifts unless BUG-SP1 is fixed). |
| TC-R02 | Timecard Report — no "NA" | After an employee clocks in on a shift created by the job, check Timecard Report. | Shift details (scheduled in/out, location) are populated, not "NA". |
| TC-R03 | Run summary in job_scheduler_run | After a run, query `SELECT * FROM job_scheduler_run ORDER BY StartedAt DESC LIMIT 1`. | All columns populated: ShiftsCreated, ShiftsSkipped, OrphanedDeleted, ResetDeleted, Status, DurationSeconds. |
| TC-R04 | Audit log drill-down | Query `SELECT * FROM job_shift_audit_log WHERE RunId = '<latest>'`. | Entries for each shift attempt: Created, Duplicate, Overlap, or Error. |

---

## 7. Data Repair Status

| Phase | Date Range | Script | Status | Records Affected |
|-------|-----------|--------|--------|-----------------|
| Phase 2 | Feb 11-16, 2026 | `repair_phase2_link_timecards.sql` | **DONE** | 728 linked; 16,049 verified no-shows |
| Phase 1 | Jan 1 — Feb 10, 2026 | `repair_phase1_jan_feb10.sql` | Dry run done — 305 matches, mostly already linked | ~305 duplicate cleanup needed |
| Duplicate cleanup | Jan 1 — Feb 10, 2026 | `cleanup_type1_duplicates.sql` | Ready to run | ~305 duplicates to deactivate |
| Phase 3 | 2025 (by quarter) | `repair_phase3_2025.sql` | Script ready, not yet run | Unknown until dry run |
| Phase 4-5 | 2024 and earlier | Not yet scripted | Pending | Unknown |
| ScheduleType NULL | All time | Not yet scripted | Pending | 1,573 in Feb 2026 alone |

---

## Fix Priority Order

Before re-enabling the scheduler job in production:

1. ~~**BUG-P1** — Add `Employeetimecard_id` guard + tomorrow-only date filter to both cleanup queries~~ **DONE (2026-02-08)**
2. **BUG-P2** — Change DELETE to soft-delete UPDATE (prevents future unrecoverable deletions)
3. **BUG-P3** — Add cleanup isolation (prevents total job failure from cleanup errors)

API fixes (separate deployment, Core API):

4. **BUG-API1** — Change `new clientscheduleshift()` to `null` in all 4 locations (2 modes × 2 APIs)
5. **BUG-API2** — Add inner try/catch around schedule matching
6. **BUG-API3** — Fix threshold=0 handling in stored procedure

Lower priority:

7. **BUG-API4** — Add ShiftId check to Manual mode
8. **BUG-API5** — Add KioskID logic to Manual mode
9. **BUG-SP1** — Fix ScheduleType NULL display

---

*End of Bug Tracker & Test Plan*
