# JMScheduler — Project Memory

**Purpose:** Persistent context so any developer or AI model can continue this project without losing what's been done. Keep this file updated as the project evolves.

---

## 1. What This Project Is

- **JMScheduler** is a .NET 8 console app that replaces the original MySQL stored procedures used to generate recurring client schedule shifts.
- It runs as a scheduled job (Windows Task Scheduler, daily). Reads from `clientschedulemodel`, writes to `clientscheduleshift` and related tables.
- **Database:** Aurora MySQL RDS (`janitorialmgr`). Shared by a web portal, mobile API, Core API, and alert jobs.

---

## 2. Project Folder Structure

```
JM Schedular/                          (workspace root)
├── PROJECT_MEMORY.md                  ← THIS FILE
├── DEPLOYMENT.md                      (Windows server deploy guide)
├── JMScheduler/                       (.NET 8 scheduler job)
│   └── src/JMScheduler.Job/
├── JM connect/JMConnectCore-main/     (Core API — ASP.NET Core 8.0, current production)
├── Production_JMConnectAPI-30/        (V30 API — ASP.NET Framework 4.8 WCF, legacy/disabled)
├── JM Portal Code/OfficePride/        (Web portal — ASP.NET MVC)
├── sql/                               (Data repair scripts)
│   ├── repair_phase1_jan_feb10.sql
│   ├── repair_phase2_link_timecards.sql
│   ├── repair_phase3_2025.sql
│   ├── cleanup_type1_duplicates.sql
│   └── daily_duplicate_check.sql
├── publish/                           (Published scheduler output)
├── schedular.sql                      (Original orchestrator SP — reference only)
├── processScheduleModel.sql           (Original weekly SP — reference only)
├── MonthlySchedular.sql               (Original monthly SP — reference only)
└── indexes                            (Existing DB indexes)
```

---

## 3. System Architecture

### Three clock-in code paths exist (each matches shifts differently):

| Path | Codebase | File | Matching Logic |
|------|----------|------|---------------|
| Mobile app (Mode=A/M) | Core API | `TimeKeeperServices.cs` | Calls `API_GetMatchingScheduleForTimecard` stored proc |
| Mobile app (Mode=A/M) | V30 API (disabled) | `Timekeeper.svc.cs` | Same proc as Core — identical logic |
| Web portal | Portal | `ClockController.cs` line 1592 | Direct LINQ: match by employee, client, date, `actualtimein == null` |

### Key database tables:

| Table | Purpose |
|-------|---------|
| `clientschedulemodel` | Recurring schedule definitions (templates) |
| `clientscheduleshift` | Individual shift instances generated from models |
| `employeetimecarddetail` | Timecard records created when employee clocks in |
| `employeepunches` | Raw punch records (in/out) |
| `employeepunchesalert` | Missing punch alerts |
| `companydetail` | Company config including `ScheduleThreshold`, `IsScheduleRestrictionEnable` |
| `clientdetail` | Location config including `ScheduleThreshold` |
| `employeedetail` | Employee config including `ScheduleThreshold` |
| `job_scheduler_run` | Scheduler run summary (created by C# job) |
| `job_shift_audit_log` | Per-shift audit trail (created by C# job) |

---

## 4. Database Environments

| Environment | Host | Purpose |
|-------------|------|---------|
| Production (latest) | Production RDS | Live data, C# job runs here |
| Source of truth | `db-only-for-investigation-cluster.cluster-cvj0ybejyco0.us-east-1.rds.amazonaws.com` | Snapshot BEFORE C# job ran. C# job confirmed never ran here (no `job_scheduler_run` table). |
| Earlier snapshot | (user has access) | Even older snapshot, also shows same orphan TC pattern |

---

## 5. Critical Bug: Orphan Timecards (Pre-existing, NOT caused by C# job)

### Root Cause

**60% of all timecards (88,231 of 145,716 in Jan 2026) are not linked to any shift** on the source-of-truth database where the C# job never ran. This is a systemic, pre-existing application bug present since V30.

### Two bugs cause this (both in `TimeKeeperServices.cs` and `Timekeeper.svc.cs`):

#### Bug 1: Silent No-Op on Untracked Entity (P0)

**Location:** `TimeKeeperServices.cs` lines 586 and 1100 (two code paths: Automatic and Manual mode)

```csharp
// CURRENT (BROKEN):
clientscheduleshift csm = new clientscheduleshift();  // Untracked by EF
// ... matching logic that may or may not reassign csm ...
if (csm != null)  // ALWAYS TRUE — new object is never null
{
    csm.Employeetimecard_id = employeeTimeCard.Id;  // Set on phantom entity
    db.SaveChanges();  // EF ignores untracked entity — shift update SILENTLY LOST
}
```

When the stored proc returns no match, `csm` remains as the untracked `new` object. EF's `SaveChanges()` ignores it. The TC (committed earlier) becomes an orphan.

**Fix:** Change `new clientscheduleshift()` to `null`.

#### Bug 2: Exception Swallowing After Partial Commit (P1)

The entire clock-in method is wrapped in a broad `catch (Exception ex)` that returns a generic error. If any exception occurs AFTER the TC is committed (line 580-584) but BEFORE the shift is linked (line 656), the TC persists as an orphan with no error logged.

**Fix:** Wrap the schedule-matching block in its own try/catch with specific logging.

#### Bug 3: Stored Proc Treats Threshold=0 as Non-NULL (P1)

`API_GetMatchingScheduleForTimecard` uses `p_schedulethreshhold is not null` to switch between date-range matching and exact-time matching. A threshold of `0` activates exact-time matching, which requires punch time to equal scheduled time to the minute — practically unmatchable. Also, the proc checks `Employeetimecard_id is null` but not `= 0`.

### ScheduleThreshold Hierarchy (3 levels, first non-null wins):

```
1. employeedetail.ScheduleThreshold
2. clientdetail.ScheduleThreshold
3. companydetail.ScheduleThreshold
```

Resolved in `TimeKeeperServices.cs` lines 356-368. If all three are NULL, `ScheudleThreshhold` stays null, and the proc uses the permissive date-range path.

---

## 6. C# Scheduler Job — What It Does

### Replaced SQL stored procedures:

| Original SP | Replaced By |
|-------------|-------------|
| `CallProcessScheduleModal` | `SchedulerJob.cs` |
| `ProcessScheduleModal` (weekly) | `WeeklyScheduleService.cs` |
| `ProcessScheduleModal_Monthly` | `MonthlyScheduleService.cs` |
| `SpanClientScheduleShift` | `MultiWeekDateCalculator.cs` |
| `ClientShiftModalEditable` | `CleanupService.ResetEditedModelAnchorsAsync` |

### Tables created by the job (3-day retention):

- `job_shift_audit_log` — Per-shift attempt (Created/Duplicate/Overlap/Error)
- `job_shift_conflicts` — Overlap conflicts (same employee, different location)
- `job_scheduler_run` — Run summary with counts

### Behavioral rules (must preserve):

- **Weekly:** Note = "Scheduled Event"; copies model claims; clones `groupschedule` row; lastrundate = NOW()
- **Monthly:** Note = "Schedule Event Monthly"; no claims copy; new `groupschedule` row; lastrundate = 1st of next month
- **ScheduleType = 1** (OpenWithAllClaim): skip duplicate check, always create
- **EmployeeId = 0:** skip overlap check (unassigned shift)
- **Overlap:** same employee, overlapping time, different ClientId → block and log
- **lastrundate:** Update for ALL loaded models, not only those that created shifts

### Bugs fixed in the C# job (do not revert):

1. Dedup key range: load existing shifts with `datetimein <= @EndDate` (not `datetimeout`)
2. Group dedup: process one representative per `GroupScheduleId`
3. lastrundate: finalize for every loaded model, not only those with new shifts
4. Monthly model load: use `DATE(csm.lastrundate) <= LAST_DAY(@ScheduleDate)`
5. BreakDetailID FK: use `NULLIF(@BreakDetailID, 0)` to avoid FK violation
6. CleanupService: only delete shifts from TOMORROW onwards (`fromdate >= DATE_ADD(CURDATE(), INTERVAL 1 DAY)`), never today or past
7. CleanupService: never delete shifts with a linked timecard (`Employeetimecard_id IS NULL OR = 0` guard on both queries)

---

## 7. Data Repair — Completed Phases

### Phase 2: Link Timecards (Feb 11-16, 2026) — DONE

- **Script:** `sql/repair_phase2_link_timecards.sql`
- **Result:** 728 records linked. 16,049 `unlinkedShifts` verified as genuine no-shows (7,354 had missing-punch alerts, 8,432 had alerting disabled, 263 had alerts not yet generated).

### Phase 1: Jan 1 — Feb 10, 2026 — DONE (Dry Run)

- **Script:** `sql/repair_phase1_jan_feb10.sql`
- **Approach:** Extract shifts with TC data from source-of-truth DB into `repair_source_jan_feb10` staging table, then match to production by composite key (employeeid, Client_id, datetimein, datetimeout).
- **Dry run result:** Only 305 matches found; 282 already had TCs linked to older (correct) shifts. The C# job's damage here was primarily creating ~305 duplicate shifts, not losing TC links.

### ScheduleType NULL Fix — IDENTIFIED

- 1,573 shifts in Feb 2026 have `ScheduleType = NULL`, causing the stored procedure `JM_GetClientSchdeulShiftRecords` to display them as "Team Scheduled" via a `CASE WHEN ... ELSE 'Team Scheduled'` fallback. This is a pre-existing bug in both old and new DBs — the C# job correctly propagates `ScheduleType` from the model, but if the model itself has NULL, the shift inherits NULL.

---

## 8. Data Repair — Pending Phases

| Phase | Period | Script | Status |
|-------|--------|--------|--------|
| Duplicate cleanup (Jan-Feb 10) | Jan 1 — Feb 10, 2026 | `cleanup_type1_duplicates.sql` | Ready to run — deactivate ~305 duplicate shifts |
| Phase 3: 2025 data | All of 2025 | `repair_phase3_2025.sql` | Script exists, not yet run |
| Phase 4-5: 2024 and earlier | Pre-2025 | Not yet scripted | Pending |
| ScheduleType NULL backfill | All time | Not yet scripted | Modify SP or backfill column |
| Ongoing duplicate check | Daily | `daily_duplicate_check.sql` | Available for monitoring |

---

## 9. Configuration & Deployment

- **Repo:** https://github.com/ranjansoni/Schedular (branch: `main`)
- **Production deploy:** See `DEPLOYMENT.md` — Windows Task Scheduler, daily at 2 AM
- **Config:** `appsettings.json` (gitignored). Template at `appsettings.example.json`.
- **Key settings:** `ConnectionStrings:SchedulerDb`, `Scheduler:AdvanceDays` (90), `Scheduler:MonthlyMonthsAhead` (12), `Scheduler:AuditRetentionDays` (3)

---

## 10. Rules for Future Work

### DO:
- Always test repair scripts with a dry-run (SELECT) before applying UPDATE/DELETE
- Use composite keys (employeeid, Client_id, datetimein, datetimeout) for shift matching — never rely on shift ID across databases
- Check `IsActive = 1` on all queries touching `clientscheduleshift`
- Verify table/column names against `information_schema` before running (e.g., `employeepunches` not `employeepunch`, `ScheduleThreshold` not `ScheduleThreshhold`)
- Use the source-of-truth DB (`db-only-for-investigation-cluster`) as the baseline for "before C# job" comparisons

### DO NOT:
- Run the C# scheduler job on the source-of-truth database
- Hard-delete shifts that have `Employeetimecard_id` linked — the original `CleanupService.cs` bug did this
- Trust `ScheduleThreshold = 0` as a meaningful config — it makes exact-time matching impossible
- Assume all orphan TCs are data damage — 60% orphan rate is a pre-existing application bug

---

*Last updated: 2026-02-08. Update this file when you make significant changes, complete repair phases, or discover new bugs.*
