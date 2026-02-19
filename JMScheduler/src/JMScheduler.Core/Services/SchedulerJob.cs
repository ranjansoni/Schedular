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
    private readonly SchedulerConfig _config;
    private readonly ILogger<SchedulerJob> _logger;

    public SchedulerJob(
        ScheduleRepository repo,
        CleanupService cleanupService,
        WeeklyScheduleService weeklyService,
        MonthlyScheduleService monthlyService,
        SchedulerConfig config,
        ILogger<SchedulerJob> logger)
    {
        _repo            = repo;
        _cleanupService  = cleanupService;
        _weeklyService   = weeklyService;
        _monthlyService  = monthlyService;
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
        public int WeeklyModelsLoaded { get; set; }
        public int AuditEntries { get; set; }
        public int Conflicts { get; set; }
        public int DurationSeconds { get; set; }
        public string? ErrorMessage { get; set; }
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

        try
        {
            // STEP 1: Ensure audit/conflict tables exist
            await _repo.EnsureAuditTablesAsync(ct);

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
            WeeklyModelsLoaded = weeklyModelsLoaded,
            AuditEntries = auditEntriesCount,
            Conflicts = conflictsCount,
            DurationSeconds = (int)totalSw.Elapsed.TotalSeconds,
            ErrorMessage = runError
        };
    }
}
