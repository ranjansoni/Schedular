using System.Diagnostics;
using Microsoft.Extensions.Logging;
using JMScheduler.Core.Configuration;
using JMScheduler.Core.Data;
using JMScheduler.Core.Models;

namespace JMScheduler.Core.Services;

/// <summary>
/// Main orchestrator — replaces CallProcessScheduleModal, ProcessScheduleModal,
/// and ProcessScheduleModal_Monthly stored procedures.
///
/// Execution flow:
///   1. Start job session (StartEvent — concurrency guard)
///   2. Ensure audit tables exist (CREATE TABLE IF NOT EXISTS)
///   3. Cleanup phase (CleanupService)
///   4. Load all active models into memory (one query per type)
///   5. Load existing shift keys into HashSet (one query — replaces per-row COUNT)
///   6. Load employee shift intervals for overlap detection (one query)
///   7. Categorize models (pre-query scan areas + claims for fast/slow path split)
///   8. Weekly processing (WeeklyScheduleService)
///   9. Monthly processing (MonthlyScheduleService)
///  10. Flush audit log + conflict entries to DB
///  11. Finalization
///  12. Complete job session (CompleteEvent)
/// </summary>
public sealed class SchedulerJob
{
    private readonly ScheduleRepository _repo;
    private readonly CleanupService _cleanupService;
    private readonly WeeklyScheduleService _weeklyService;
    private readonly MonthlyScheduleService _monthlyService;
    private readonly MultiWeekDateCalculator _multiWeekCalc;
    private readonly SchedulerConfig _config;
    private readonly ILogger<SchedulerJob> _logger;

    public SchedulerJob(
        ScheduleRepository repo,
        CleanupService cleanupService,
        WeeklyScheduleService weeklyService,
        MonthlyScheduleService monthlyService,
        MultiWeekDateCalculator multiWeekCalc,
        SchedulerConfig config,
        ILogger<SchedulerJob> logger)
    {
        _repo            = repo;
        _cleanupService  = cleanupService;
        _weeklyService   = weeklyService;
        _monthlyService  = monthlyService;
        _multiWeekCalc   = multiWeekCalc;
        _config          = config;
        _logger          = logger;
    }

    /// <summary>
    /// Result returned after a scheduler run completes. Used by both the console app and the API.
    /// </summary>
    public sealed class RunResult
    {
        public string RunId { get; set; } = string.Empty;
        public string Status { get; set; } = "Completed";
        public int ShiftsCreated { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int OverlapsBlocked { get; set; }
        public int OrphanedDeleted { get; set; }
        public int ResetDeleted { get; set; }
        public int ResetShiftsDeleted { get; set; }
        public int WeeklyModelsLoaded { get; set; }
        public int AuditEntries { get; set; }
        public int Conflicts { get; set; }
        public int DurationSeconds { get; set; }
        public string? ErrorMessage { get; set; }
    }

    // ========================================================================
    // LEAN SINGLE-MODEL PATH
    // Called by the portal for real-time model changes. No audit log, no
    // concurrency guard, no cleanup, no overlap detection. Just:
    //   1. Load the one model
    //   2. Optionally delete future unlinked shifts (reset)
    //   3. Generate new shifts
    //   4. Insert shifts (+ scan areas / claims / groups)
    //   5. Update lastrundate
    // ========================================================================

    /// <summary>
    /// Fast, targeted run for a single model. Designed for portal on-demand calls
    /// when a user adds or edits a model. Skips all batch overhead.
    /// </summary>
    public async Task<RunResult> RunSingleModelAsync(
        int modelId,
        bool reset,
        int advanceDaysOverride = 0,
        int monthlyMonthsAheadOverride = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = $"single-{modelId}-{DateTime.Now:yyyyMMddHHmmss}";

        int effectiveAdvanceDays = advanceDaysOverride > 0 ? advanceDaysOverride : _config.AdvanceDays;
        int effectiveMonthlyMonths = monthlyMonthsAheadOverride > 0 ? monthlyMonthsAheadOverride : _config.MonthlyMonthsAhead;

        _logger.LogInformation(
            "RunSingleModel starting: ModelId={ModelId}, Reset={Reset}, AdvanceDays={Days}, MonthlyMonths={Months}",
            modelId, reset, effectiveAdvanceDays, effectiveMonthlyMonths);

        int resetShiftsDeleted = 0;
        int shiftsCreated = 0;
        int duplicatesSkipped = 0;

        try
        {
            // 1. Load the model (no lastrundate filter — this is on-demand)
            var model = await _repo.LoadModelByIdAsync(modelId, ct);
            if (model == null)
            {
                _logger.LogWarning("RunSingleModel: ModelId={ModelId} not found or inactive", modelId);
                return new RunResult
                {
                    RunId = sessionId,
                    Status = "NotFound",
                    ErrorMessage = $"Model {modelId} not found, inactive, or its client/company is inactive.",
                    DurationSeconds = (int)sw.Elapsed.TotalSeconds
                };
            }

            _logger.LogInformation(
                "Loaded model {ModelId}: RecurringType={Type}, Client={Client}, Employee={Emp}, Group={Group}",
                model.Id, model.RecurringType, model.Client_id, model.EmployeeId, model.GroupScheduleId);

            // 2. Reset: delete future unlinked shifts
            if (reset)
            {
                resetShiftsDeleted = await _repo.DeleteFutureShiftsForModelAsync(modelId, ct);
                _logger.LogInformation("Reset: deleted {Count} future shifts for ModelId={ModelId}",
                    resetShiftsDeleted, modelId);
            }

            // 3. Load scoped dedup keys (client-scoped is tiny compared to global)
            var now = DateTime.Now;
            var endDate = model.RecurringType == 1
                ? now.AddMonths(effectiveMonthlyMonths).AddDays(1)
                : now.AddDays(effectiveAdvanceDays + 1);

            var existingKeys = await _repo.LoadExistingShiftKeysForClientAsync(model.Client_id, now, endDate, ct);
            var existingModalKeys = await _repo.LoadExistingModalShiftKeysForModelAsync(modelId, now, endDate, ct);

            _logger.LogInformation("Loaded scoped keys: standard={Std}, modal={Modal}",
                existingKeys.Count, existingModalKeys.Count);

            // 4. Check scan areas / claims for this model
            bool hasScanAreas = await _repo.HasScanAreasAsync(modelId, ct);
            bool hasClaims = await _repo.HasClaimsAsync(modelId, ct);
            bool hasGroup = model.HasGroupSchedule;

            // 5. Generate + insert shifts based on recurring type
            if (model.RecurringType == 0)
            {
                // === WEEKLY (including multi-week) ===
                string noteText = "Scheduled Event";

                HashSet<DateTime>? multiWeekValidDates = null;
                if (model.IsMultiWeek)
                {
                    var ids = new List<int> { modelId };
                    var lastShiftDates = await _repo.GetLastShiftDatesForModelsAsync(ids, ct);
                    var lastHistoryDates = await _repo.GetLastHistoryDatesForModelsAsync(ids, ct);
                    var tracking = await _repo.LoadMultiWeekTrackingAsync(ct);
                    tracking.TryGetValue(modelId, out var trackStatus);

                    DateTime? lastShift = lastShiftDates.TryGetValue(modelId, out var sd) ? sd : null;
                    DateTime? lastHistory = lastHistoryDates.TryGetValue(modelId, out var hd) ? hd : null;

                    var (anchor, restriction) = _multiWeekCalc.ResolveAnchorAndRestriction(
                        model, trackStatus, lastShift, lastHistory);
                    multiWeekValidDates = _multiWeekCalc.CalculateValidDates(
                        model, anchor, restriction, effectiveAdvanceDays);

                    _logger.LogInformation("Multi-week model: {Count} valid dates computed", multiWeekValidDates.Count);
                }

                var fastPathShifts = new List<ScheduleShift>();
                var scanAreaShifts = new List<ScheduleShift>();
                var claimsShifts = new List<ScheduleShift>();
                var groupDates = new List<DateTime>();

                for (int dayOffset = 0; dayOffset <= effectiveAdvanceDays; dayOffset++)
                {
                    var targetDate = now.Date.AddDays(dayOffset);

                    if (!model.IsScheduledForDay(targetDate.DayOfWeek)) continue;
                    if (model.StartDate.Date > now.Date) continue;
                    if (!model.HasNoEndDate && model.EndDate.Date < targetDate.Date) continue;

                    if (model.IsMultiWeek && multiWeekValidDates != null
                        && !multiWeekValidDates.Contains(targetDate.Date))
                        continue;

                    var shift = ScheduleShift.FromModel(model, targetDate, noteText);
                    var key = shift.GetDuplicateKey();
                    var modalKey = shift.GetModalDuplicateKey();

                    bool isDuplicate = model.ScheduleType == 1
                        ? existingModalKeys.Contains(modalKey)
                        : existingKeys.Contains(key);

                    if (isDuplicate)
                    {
                        duplicatesSkipped++;
                        continue;
                    }

                    existingKeys.Add(key);
                    existingModalKeys.Add(modalKey);

                    if (hasGroup)
                    {
                        groupDates.Add(targetDate);
                    }
                    else if (hasClaims)
                    {
                        claimsShifts.Add(shift);
                    }
                    else if (hasScanAreas)
                    {
                        scanAreaShifts.Add(shift);
                    }
                    else
                    {
                        fastPathShifts.Add(shift);
                    }
                }

                // Insert fast-path shifts
                if (fastPathShifts.Count > 0)
                {
                    shiftsCreated += await _repo.BulkInsertShiftsAsync(fastPathShifts, ct);
                }

                // Insert scan-area shifts + copy scan areas
                if (scanAreaShifts.Count > 0)
                {
                    shiftsCreated += await _repo.BulkInsertShiftsAsync(scanAreaShifts, ct);
                    foreach (var shift in scanAreaShifts)
                        await _repo.BulkCopyScanAreasAsync(new List<int> { modelId }, shift.DateTimeIn.Date, ct);
                }

                // Insert claims shifts + copy claims + optional scan areas
                if (claimsShifts.Count > 0)
                {
                    shiftsCreated += await _repo.BulkInsertShiftsAsync(claimsShifts, ct);
                    foreach (var shift in claimsShifts)
                    {
                        await _repo.BulkCopyClaimsAsync(new List<int> { modelId }, shift.DateTimeIn.Date, ct);
                        if (hasScanAreas)
                            await _repo.BulkCopyScanAreasAsync(new List<int> { modelId }, shift.DateTimeIn.Date, ct);
                    }
                }

                // Insert group shifts (individual path — needs LAST_INSERT_ID for group clone)
                foreach (var targetDate in groupDates)
                {
                    await using var conn = await _repo.CreateConnectionAsync(ct);
                    int newGroupId = await _repo.CloneGroupScheduleAsync(conn, model.GroupScheduleId, ct);
                    var insertedIds = await _repo.InsertGroupShiftsAsync(
                        conn, model.GroupScheduleId, newGroupId, targetDate, noteText,
                        existingKeys, existingModalKeys, ct);
                    shiftsCreated += insertedIds.Count;
                }

                // Update lastrundate
                await _repo.UpdateSingleModelLastRunDateAsync(modelId, ct);

                // Finalize multi-week tracking if needed
                if (model.IsMultiWeek && shiftsCreated > 0)
                {
                    var lastShiftDate = await _repo.GetLastShiftDateForModelAsync(modelId, ct);
                    if (lastShiftDate.HasValue)
                        await _repo.FinalizeMultiWeekTrackingAsync(modelId, lastShiftDate.Value, ct);
                }
            }
            else if (model.RecurringType == 1)
            {
                // === MONTHLY ===
                string noteText = "Schedule Event Monthly";

                DayOfWeek? scheduledDay = GetScheduledDayOfWeek(model);
                if (scheduledDay == null)
                {
                    _logger.LogWarning("Monthly model {ModelId} has no day-of-week flag set", modelId);
                    return new RunResult
                    {
                        RunId = sessionId,
                        Status = "Skipped",
                        ErrorMessage = "Monthly model has no day-of-week flag set.",
                        DurationSeconds = (int)sw.Elapsed.TotalSeconds
                    };
                }

                for (int monthOffset = 0; monthOffset < effectiveMonthlyMonths; monthOffset++)
                {
                    var targetMonth = now.AddMonths(monthOffset);
                    var monthStart = new DateTime(targetMonth.Year, targetMonth.Month, 1);

                    DateTime? targetDate = MonthlyScheduleService.CalculateNthWeekdayOfMonth(
                        monthStart, scheduledDay.Value, model.MonthlyRecurringType);

                    if (targetDate == null) continue;
                    if (targetDate.Value.Date < model.StartDate.Date) continue;

                    var shift = ScheduleShift.FromModel(model, targetDate.Value, noteText);
                    var key = shift.GetDuplicateKey();
                    var modalKey = shift.GetModalDuplicateKey();

                    bool isDuplicate = model.ScheduleType == 1
                        ? existingModalKeys.Contains(modalKey)
                        : existingKeys.Contains(key);

                    if (isDuplicate)
                    {
                        duplicatesSkipped++;
                        continue;
                    }

                    existingKeys.Add(key);
                    existingModalKeys.Add(modalKey);

                    if (hasGroup)
                    {
                        await using var conn = await _repo.CreateConnectionAsync(ct);
                        int newGroupId = await _repo.InsertNewGroupScheduleAsync(conn, model.Client_id, ct);
                        var insertedIds = await _repo.InsertMonthlyGroupShiftsAsync(
                            conn, model.GroupScheduleId, newGroupId, targetDate.Value,
                            existingKeys, existingModalKeys, ct);
                        shiftsCreated += insertedIds.Count;
                    }
                    else
                    {
                        await using var conn = await _repo.CreateConnectionAsync(ct);
                        long shiftId = await _repo.InsertShiftAndGetIdAsync(conn, shift, ct);
                        shiftsCreated++;

                        if (hasScanAreas)
                            await _repo.CallProcessRecurringScanAreaAsync(conn, modelId, shiftId, ct);
                    }
                }

                // Monthly sets lastrundate to 1st of next month
                await _repo.UpdateSingleModelLastRunDateAsync(modelId, ct);
            }
            else
            {
                _logger.LogWarning("Unknown RecurringType={Type} for ModelId={ModelId}", model.RecurringType, modelId);
            }

            sw.Stop();
            _logger.LogInformation(
                "RunSingleModel completed: ModelId={ModelId}, ShiftsCreated={Created}, Duplicates={Dupes}, " +
                "ResetDeleted={Reset}, Duration={Duration:F1}s",
                modelId, shiftsCreated, duplicatesSkipped, resetShiftsDeleted, sw.Elapsed.TotalSeconds);

            return new RunResult
            {
                RunId = sessionId,
                Status = "Completed",
                ShiftsCreated = shiftsCreated,
                DuplicatesSkipped = duplicatesSkipped,
                ResetShiftsDeleted = resetShiftsDeleted,
                WeeklyModelsLoaded = 1,
                DurationSeconds = (int)sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "RunSingleModel failed for ModelId={ModelId}: {Message}", modelId, ex.Message);
            return new RunResult
            {
                RunId = sessionId,
                Status = "Failed",
                ShiftsCreated = shiftsCreated,
                ResetShiftsDeleted = resetShiftsDeleted,
                DurationSeconds = (int)sw.Elapsed.TotalSeconds,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Determine which day of the week a model is scheduled for (used by monthly lean path).
    /// </summary>
    private static DayOfWeek? GetScheduledDayOfWeek(ScheduleModel model)
    {
        if (model.Monday)    return DayOfWeek.Monday;
        if (model.Tuesday)   return DayOfWeek.Tuesday;
        if (model.Wednesday) return DayOfWeek.Wednesday;
        if (model.Thursday)  return DayOfWeek.Thursday;
        if (model.Friday)    return DayOfWeek.Friday;
        if (model.Saturday)  return DayOfWeek.Saturday;
        if (model.Sunday)    return DayOfWeek.Sunday;
        return null;
    }

    /// <summary>
    /// Run the complete scheduling job. This is the entry point called from Program.cs (console)
    /// or the API controller.
    /// </summary>
    /// <param name="scheduleDateTime">The base schedule date/time. Typically DateTime.Now.</param>
    /// <param name="companyId">If > 0, only process models for this company. 0 = all.</param>
    /// <param name="modelId">If > 0, only process this specific model. 0 = all.</param>
    /// <param name="advanceDaysOverride">If > 0, override AdvanceDays from config. 0 = use config.</param>
    /// <param name="monthlyMonthsAheadOverride">If > 0, override MonthlyMonthsAhead from config. 0 = use config.</param>
    /// <param name="ct">Cancellation token for graceful shutdown.</param>
    public async Task<RunResult> RunAsync(
        DateTime scheduleDateTime,
        int companyId = 0,
        int modelId = 0,
        int advanceDaysOverride = 0,
        int monthlyMonthsAheadOverride = 0,
        bool reset = false,
        CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var sessionId = Guid.NewGuid().ToString();
        var startTime = DateTime.Now;

        int effectiveAdvanceDays = advanceDaysOverride > 0 ? advanceDaysOverride : _config.AdvanceDays;
        int effectiveMonthlyMonths = monthlyMonthsAheadOverride > 0 ? monthlyMonthsAheadOverride : _config.MonthlyMonthsAhead;

        int orphanedDeleted = 0, resetDeleted = 0;
        int weeklyModelsLoaded = 0, recordsConsidered = 0, shiftsCreated = 0, shiftsSkipped = 0;
        int auditEntriesCount = 0, conflictsCount = 0;
        string runStatus = "Completed";
        string? runError = null;

        _logger.LogInformation(
            "========================================================================");
        _logger.LogInformation(
            "JMScheduler job starting. SessionId={SessionId}, ScheduleDate={Date:yyyy-MM-dd HH:mm}, " +
            "AdvanceDays={AdvanceDays}, MonthlyMonthsAhead={Monthly}, CompanyId={CompanyId}, ModelId={ModelId}",
            sessionId, scheduleDateTime, effectiveAdvanceDays, effectiveMonthlyMonths, companyId, modelId);
        _logger.LogInformation(
            "========================================================================");

        // STEP 0: Concurrency guard (StartEvent)
        var canRun = await _repo.StartJobSessionAsync(sessionId, startTime, "ShiftSchedular", ct);
        if (!canRun)
        {
            _logger.LogWarning(
                "StartEvent returned 0 — another instance may already be running. Exiting.");
            return new RunResult
            {
                RunId = sessionId,
                Status = "Blocked",
                ErrorMessage = "Another instance is already running."
            };
        }

        await _repo.LogJobTrackingAsync("C# SchedulerJob: started", ct);
        await _repo.InsertRunSummaryAsync(sessionId, startTime, ct);

        int resetShiftsDeleted = 0;

        try
        {
            // STEP 1: Ensure audit/conflict tables exist
            await _repo.EnsureAuditTablesAsync(ct);

            // STEP 1.5: If reset=true + modelId, delete future unlinked shifts first
            if (reset && modelId > 0)
            {
                _logger.LogInformation(
                    "Reset mode: deleting future unlinked shifts for ModelId={ModelId} before regenerating",
                    modelId);
                resetShiftsDeleted = await _repo.DeleteFutureShiftsForModelAsync(modelId, ct);
                await _repo.LogJobTrackingAsync(
                    $"C# reset: deleted {resetShiftsDeleted} future shifts for ModelId={modelId}", ct);
            }

            // STEP 2: Cleanup phase
            (orphanedDeleted, resetDeleted) = await _cleanupService.RunAsync(ct);
            await _repo.LogJobTrackingAsync(
                $"C# cleanup: orphaned={orphanedDeleted}, reset={resetDeleted}", ct);

            // STEP 3: Load weekly models (with optional company/model filter)
            var phaseSw = Stopwatch.StartNew();
            var weeklyModels = await _repo.LoadWeeklyModelsAsync(scheduleDateTime, ct, companyId, modelId);
            phaseSw.Stop();
            weeklyModelsLoaded = weeklyModels.Count;
            _logger.LogInformation("Model loading completed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // STEP 4: Load existing shift keys for duplicate detection
            phaseSw.Restart();
            var endDate = scheduleDateTime.AddDays(effectiveAdvanceDays + 1);
            var monthlyEndDate = scheduleDateTime.AddMonths(effectiveMonthlyMonths).AddDays(1);
            if (monthlyEndDate > endDate)
                endDate = monthlyEndDate;

            var existingKeys = await _repo.LoadExistingShiftKeysAsync(scheduleDateTime, endDate, ct);
            var existingModalKeys = await _repo.LoadExistingModalShiftKeysAsync(scheduleDateTime, endDate, ct);
            phaseSw.Stop();
            _logger.LogInformation(
                "Shift key loading completed in {Elapsed:F1}s (standard={Standard}, modal={Modal})",
                phaseSw.Elapsed.TotalSeconds, existingKeys.Count, existingModalKeys.Count);

            // STEP 4.5: Load employee shift intervals for overlap detection
            phaseSw.Restart();
            var overlapDetector = new OverlapDetector();
            var intervals = await _repo.LoadEmployeeShiftIntervalsAsync(scheduleDateTime, endDate, ct);
            overlapDetector.Load(intervals);
            phaseSw.Stop();
            _logger.LogInformation(
                "Overlap detector loaded in {Elapsed:F1}s: {Intervals} intervals for {Employees} employees",
                phaseSw.Elapsed.TotalSeconds, overlapDetector.TotalIntervals, overlapDetector.UniqueEmployees);

            // STEP 5: Pre-load model categorization for fast/slow path
            phaseSw.Restart();
            var modelsWithScanAreas = await _repo.LoadModelsWithScanAreasAsync(ct);
            var modelsWithClaims = await _repo.LoadModelsWithClaimsAsync(ct);
            var multiWeekTracking = await _repo.LoadMultiWeekTrackingAsync(ct);
            phaseSw.Stop();
            _logger.LogInformation(
                "Model categorization loaded in {Elapsed:F1}s (scanAreas={ScanAreas}, claims={Claims}, multiWeek={MultiWeek})",
                phaseSw.Elapsed.TotalSeconds, modelsWithScanAreas.Count,
                modelsWithClaims.Count, multiWeekTracking.Count);

            await _repo.LogJobTrackingAsync(
                $"C# loaded: weeklyModels={weeklyModels.Count}, existingKeys={existingKeys.Count}, " +
                $"overlapIntervals={overlapDetector.TotalIntervals}", ct);

            var auditEntries = new List<ShiftAuditEntry>();
            var conflicts = new List<ShiftConflict>();

            // STEP 6: Weekly processing (with effective advance days)
            WeeklyScheduleService.WeeklyResult? weeklyResult = null;

            if (weeklyModels.Count > 0)
            {
                weeklyResult = await _weeklyService.ProcessAsync(
                    weeklyModels, scheduleDateTime, existingKeys, existingModalKeys,
                    modelsWithScanAreas, modelsWithClaims, multiWeekTracking,
                    sessionId, startTime, overlapDetector, auditEntries, conflicts,
                    effectiveAdvanceDays, ct);

                await _repo.LogJobTrackingAsync(
                    $"C# weekly: created={weeklyResult.TotalShiftsCreated}, " +
                    $"fast={weeklyResult.FastPathInserted}, slow={weeklyResult.SlowPathInserted}, " +
                    $"dupes={weeklyResult.DuplicatesSkipped}, overlaps={weeklyResult.OverlapsBlocked}", ct);
            }
            else
            {
                _logger.LogInformation("No weekly models to process");
            }

            // STEP 7: Monthly processing (with optional company/model filter and effective months)
            var monthlyResult = await _monthlyService.ProcessAsync(
                scheduleDateTime, existingKeys, existingModalKeys, modelsWithScanAreas,
                sessionId, startTime, overlapDetector, auditEntries, conflicts,
                effectiveMonthlyMonths, companyId, modelId, ct);

            await _repo.LogJobTrackingAsync(
                $"C# monthly: created={monthlyResult.TotalShiftsCreated}, " +
                $"dupes={monthlyResult.DuplicatesSkipped}, overlaps={monthlyResult.OverlapsBlocked}", ct);

            // STEP 8: Flush audit log and conflict entries to DB
            phaseSw.Restart();
            _logger.LogInformation(
                "Flushing audit data: {AuditCount} audit entries, {ConflictCount} conflicts",
                auditEntries.Count, conflicts.Count);

            await _repo.BulkInsertAuditLogAsync(auditEntries, ct);
            await _repo.BulkInsertConflictsAsync(conflicts, ct);
            phaseSw.Stop();
            _logger.LogInformation("Audit data flushed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // STEP 9: Finalization
            phaseSw.Restart();

            if (weeklyModels.Count > 0)
            {
                var allWeeklyModelIds = weeklyModels.Select(m => m.Id);
                await _repo.UpdateWeeklyLastRunDatesAsync(allWeeklyModelIds, ct);
            }

            if (weeklyResult?.MultiWeekModelsWithChanges.Count > 0)
            {
                foreach (int mId in weeklyResult.MultiWeekModelsWithChanges)
                {
                    var lastShiftDate = await _repo.GetLastShiftDateForModelAsync(mId, ct);
                    if (lastShiftDate.HasValue)
                    {
                        await _repo.FinalizeMultiWeekTrackingAsync(mId, lastShiftDate.Value, ct);
                    }
                }

                _logger.LogInformation("Finalized multi-week tracking for {Count} models",
                    weeklyResult.MultiWeekModelsWithChanges.Count);
            }

            foreach (var (monthStart, modelIds) in monthlyResult.AllLoadedModelsByMonth)
            {
                await _repo.UpdateMonthlyLastRunDatesAsync(modelIds, monthStart, ct);
            }

            await _repo.CleanupAuditTablesAsync(_config.AuditRetentionDays, ct);
            _logger.LogInformation("Audit table cleanup completed (retention: {Days} days)",
                _config.AuditRetentionDays);

            phaseSw.Stop();
            _logger.LogInformation("Finalization completed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // Summary
            shiftsCreated = (weeklyResult?.TotalShiftsCreated ?? 0) + monthlyResult.TotalShiftsCreated;
            int totalDupes = (weeklyResult?.DuplicatesSkipped ?? 0) + monthlyResult.DuplicatesSkipped;
            int totalOverlaps = (weeklyResult?.OverlapsBlocked ?? 0) + monthlyResult.OverlapsBlocked;
            shiftsSkipped = totalDupes + totalOverlaps;
            recordsConsidered = auditEntries.Count;
            auditEntriesCount = auditEntries.Count;
            conflictsCount = conflicts.Count;

            _logger.LogInformation(
                "JOB SUMMARY: Total shifts created = {Total} (weekly={Weekly}, monthly={Monthly}), " +
                "Duplicates skipped = {Dupes}, Overlaps blocked = {Overlaps}, " +
                "Orphaned deleted = {Orphaned}, Reset deleted = {Reset}, " +
                "Audit entries = {AuditCount}, Conflicts = {ConflictCount}",
                shiftsCreated,
                weeklyResult?.TotalShiftsCreated ?? 0,
                monthlyResult.TotalShiftsCreated,
                totalDupes,
                totalOverlaps,
                orphanedDeleted,
                resetDeleted,
                auditEntriesCount,
                conflictsCount);

            await _repo.LogJobTrackingAsync(
                $"C# SchedulerJob: completed. Total={shiftsCreated}, Dupes={totalDupes}, " +
                $"Overlaps={totalOverlaps}, Audit={auditEntriesCount}, Conflicts={conflictsCount}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            runStatus = "Cancelled";
            runError = "Job was cancelled.";
            _logger.LogWarning("Scheduler job was cancelled");
            await _repo.LogJobTrackingAsync("C# SchedulerJob: CANCELLED", ct);
            throw;
        }
        catch (Exception ex)
        {
            runStatus = "Failed";
            runError = ex.Message;
            _logger.LogError(ex, "Scheduler job failed with error: {Message}", ex.Message);
            await _repo.LogJobTrackingAsync($"C# SchedulerJob: FAILED - {ex.Message}", ct);
            throw;
        }
        finally
        {
            // STEP 10: Update run summary and complete job session
            totalSw.Stop();
            var endTime = DateTime.Now;
            var elapsedSeconds = (int)totalSw.Elapsed.TotalSeconds;

            await _repo.UpdateRunSummaryAsync(
                sessionId, endTime, elapsedSeconds, runStatus,
                weeklyModelsLoaded, recordsConsidered, shiftsCreated, shiftsSkipped,
                orphanedDeleted, resetDeleted, auditEntriesCount, conflictsCount,
                runError, ct);

            try
            {
                await _repo.CompleteJobSessionAsync(sessionId, endTime, elapsedSeconds, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call CompleteEvent");
            }

            _logger.LogInformation(
                "========================================================================");
            _logger.LogInformation(
                "JMScheduler job finished. Total time: {Elapsed} ({Seconds}s). SessionId={SessionId}",
                totalSw.Elapsed, elapsedSeconds, sessionId);
            _logger.LogInformation(
                "========================================================================");
        }

        return new RunResult
        {
            RunId = sessionId,
            Status = runStatus,
            ShiftsCreated = shiftsCreated,
            DuplicatesSkipped = (int)((shiftsSkipped > 0) ? shiftsSkipped : 0),
            OrphanedDeleted = orphanedDeleted,
            ResetDeleted = resetDeleted,
            ResetShiftsDeleted = resetShiftsDeleted,
            WeeklyModelsLoaded = weeklyModelsLoaded,
            AuditEntries = auditEntriesCount,
            Conflicts = conflictsCount,
            DurationSeconds = (int)totalSw.Elapsed.TotalSeconds,
            ErrorMessage = runError
        };
    }
}
