# JMScheduler — Project Memory

**Purpose:** Persistent context so you (or another dev) can continue on a different laptop or account without losing what’s been done. Keep this file updated as the project evolves. It lives in the repo and is pushed to GitHub.

---

## 1. What This Project Is

- **JMScheduler** is a .NET 8 console app that **replaces** the original MySQL stored procedures used to generate recurring client schedule shifts.
- It runs as a **scheduled job** (e.g. Windows Task Scheduler, daily). It reads from `clientschedulemodel`, writes to `clientscheduleshift` and related tables, and respects web/mobile/alert traffic (batch sizes, sleep, deadlock retry).
- **Database:** Aurora MySQL RDS. Same DB is used by a web app, mobile app, and an alert job (every 5 min).

---

## 2. What Was Replaced (SQL → C#)

| Original artifact | Location in repo | Replaced by |
|------------------|------------------|-------------|
| `CallProcessScheduleModal` | `schedular.sql` | `SchedulerJob.cs` (orchestrator) |
| `ProcessScheduleModal` (weekly/multi-week) | `processScheduleModel.sql` | `WeeklyScheduleService.cs` |
| `ProcessScheduleModal_Monthly` | `MonthlySchedular.sql` | `MonthlyScheduleService.cs` |
| `SpanClientScheduleShift` (multi-week dates) | `SpanClientScheduleShift` | `MultiWeekDateCalculator.cs` (in-memory) |
| `ClientShiftModalEditable` (reset anchors) | `ClientShiftModalEditable` | `CleanupService.ResetEditedModelAnchorsAsync` |
| **Kept as SP** (called from C#) | `ProcessRecurring_ScheduleScanArea` | Called per shift on slow path; bulk copy used where possible via `BulkCopyScanAreasAsync` |
| **Kept** | `StartEvent` / `CompleteEvent` | Called from C# for concurrency guard and job completion |

---

## 3. Repo Layout

```
JM Schedular/                    (repo root)
├── PROJECT_MEMORY.md            ← this file
├── .gitignore                   (excludes appsettings.json, logs, bin/obj)
├── schedular.sql                (original orchestrator SP — reference)
├── processScheduleModel.sql     (original weekly SP — reference)
├── MonthlySchedular.sql         (original monthly SP — reference)
├── SpanClientScheduleShift      (original multi-week SP — reference)
├── ClientShiftModalEditable     (original — reference)
├── ProcessRecurring_ScheduleScanArea
├── indexes                      (existing DB indexes)
├── recommended_indexes.sql
└── JMScheduler/
    ├── JMScheduler.sln
    └── src/JMScheduler.Job/
        ├── Program.cs
        ├── appsettings.example.json   (committed; copy to appsettings.json locally)
        ├── appsettings.json           (gitignored — real connection string)
        ├── Configuration/SchedulerConfig.cs
        ├── Data/ScheduleRepository.cs
        ├── Infrastructure/DbConnectionFactory.cs, DeadlockRetryHandler.cs
        ├── Models/ (ScheduleModel, ScheduleShift, NextRunStatus, ShiftAuditEntry, ShiftConflict)
        └── Services/
            ├── SchedulerJob.cs        (orchestrator)
            ├── CleanupService.cs
            ├── WeeklyScheduleService.cs
            ├── MonthlyScheduleService.cs
            ├── MultiWeekDateCalculator.cs
            └── OverlapDetector.cs
```

---

## 4. Database Tables (Summary)

**Read:** `clientschedulemodel`, `clientdetail`, `companydetail`, `employeescheduleshiftmodelclaim`, `scheduleshiftscandetailmodel`, `scheduleshiftscantaskdetailmodel`, `groupschedule`, `job_ClientscheduleShiftnextrunStatus`, `job_clientschedulefunctiondataHistory`, `clientscheduleshift` (for existing keys and overlap intervals).

**Write/update:** `clientscheduleshift`, `employeescheduleshiftclaim`, `groupschedule`, scan tables (via SP or bulk), `clientschedulemodel.lastrundate`, `job_ClientscheduleShiftnextrunStatus`, `logs.jobtracking`.

**New tables (created by job at startup if missing):**

- `job_shift_audit_log` — Every shift attempt per run (Created/Duplicate/Overlap/Error), model type, recurring pattern. 3-day retention.
- `job_shift_conflicts` — Overlap conflicts (same employee, different location, overlapping time). 3-day retention.
- `job_scheduler_run` — **Master per run:** RunId (PK), StartedAt, CompletedAt, DurationSeconds, Status, WeeklyModelsLoaded, RecordsConsidered, ShiftsCreated, ShiftsSkipped, OrphanedDeleted, ResetDeleted, AuditEntriesCount, ConflictsCount, ErrorMessage. Used for portal drill-down by RunId. 3-day retention.

---

## 5. Configuration (appsettings)

- **ConnectionStrings:SchedulerDb** — MySQL connection string (never commit; use `appsettings.example.json` as template).
- **Scheduler:AdvanceDays** — Days ahead for weekly/multi-week (e.g. 90).
- **Scheduler:MonthlyMonthsAhead** — Months ahead for monthly (e.g. 12).
- **Scheduler:AuditRetentionDays** — Retention for audit log, conflicts, and run summary (default 3).
- **Scheduler:InsertBatchSize**, **DeleteBatchSize**, **SleepBetweenBatchesMs**, **MaxDeadlockRetries**, **DeadlockRetryBaseDelayMs**, **HistoryRetentionDays** — Tuning; see `SchedulerConfig.cs`.

Env vars prefixed with `JM_` can override config.

---

## 6. How to Run

1. Clone repo, then:
   - `cp JMScheduler/src/JMScheduler.Job/appsettings.example.json JMScheduler/src/JMScheduler.Job/appsettings.json`
   - Edit `appsettings.json` with real connection string.
2. Build: `dotnet build -c Release` (from solution or project dir).
3. Run: `dotnet run --project JMScheduler/src/JMScheduler.Job` or run the built exe. Optional: pass a datetime as first argument for testing.
4. **Windows production:** Schedule the exe (e.g. Task Scheduler daily at 2 AM), ensure `appsettings.json` is next to the exe.

---

## 7. Behavioral Rules (Must Preserve)

- **Weekly:** Note = "Scheduled Event"; copies model claims to shift claims; group = clone existing `groupschedule` row; lastrundate = NOW().
- **Monthly:** Note = "Schedule Event Monthly"; does **not** copy claims; group = new `groupschedule` row (IsEmployeeSchedule=1, IsClientSchedule=0); lastrundate = 1st of next month.
- **ScheduleType = 1** (OpenWithAllClaim): skip duplicate check, always create.
- **EmployeeId = 0:** skip overlap check (unassigned shift).
- **Overlap:** same employee, overlapping time, **different** ClientId → block and log to `job_shift_conflicts` and audit log.
- **lastrundate:** Update for **all** loaded models (weekly and monthly), not only those that created shifts; otherwise models with all-duplicate shifts never advance and get re-loaded every day.

---

## 8. Bugs Fixed (Do Not Revert)

1. **Dedup key range:** Existing shift keys must be loaded with `datetimein <= @EndDate` (not `datetimeout <= @EndDate`), or long-duration shifts are excluded and duplicates appear.
2. **Group dedup:** When multiple models share the same `GroupScheduleId`, process **one** representative per group (e.g. by `GroupScheduleId`) before calling group insert; otherwise the same group is inserted N times.
3. **lastrundate:** Finalization must update lastrundate for every weekly model that was **loaded** and for every monthly model in `AllLoadedModelsByMonth`, not only those that created new shifts.
4. **Monthly model load:** Monthly models are loaded when `DATE(csm.lastrundate) <= LAST_DAY(@ScheduleDate)` (or equivalent) so reset/multi-month runs work correctly.
5. **BreakDetailID FK:** Use `NULLIF(@BreakDetailID, 0)` on insert so 0 becomes NULL and does not violate FK to `breakdetail`.

---

## 9. Optional / Future

- **Portal:** Drill-down by RunId: list `job_scheduler_run`, then show `job_shift_audit_log` and `job_shift_conflicts` for selected run.
- **README:** Add a short README in repo root (build, run, config, DB requirements).
- **Alerts:** Monitor `job_scheduler_run.Status = 'Failed'` or `logs.jobtracking` for failure alerts.

---

## 10. GitHub

- Repo: **https://github.com/ranjansoni/Schedular**
- Branch: **main**
- `appsettings.json` and `logs/` are gitignored; only `appsettings.example.json` is committed.

---

*Last updated: 2026-02 (initial project memory). Update this file when you make significant changes or fix important bugs.*
