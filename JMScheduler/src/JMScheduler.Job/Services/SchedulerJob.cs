using System.Diagnostics;
using Microsoft.Extensions.Logging;
using JMScheduler.Job.Configuration;
using JMScheduler.Job.Data;
using JMScheduler.Job.Models;

namespace JMScheduler.Job.Services;

/// <summary>
/// Main orchestrator — replaces CallProcessScheduleModal, ProcessScheduleModal,
/// and ProcessScheduleModal_Monthly stored procedures.
///
/// Execution flow:
///   1. Start job session (StartEvent — concurrency guard)
///   2. Ensure audit tables exist (CREATE TABLE IF NOT EXISTS)
///   3. Cleanup phase (CleanupService)
///      - Seed multi-week tracking for new models
///      - Batch-delete orphaned + reset/inactive shifts
///      - Reset edited model anchors (ClientShiftModalEditable logic)
///      - Clear IsModelReset flags
///      - Clean working tables + prune history
///   4. Load all active models into memory (one query per type)
///   5. Load existing shift keys into HashSet (one query — replaces per-row COUNT)
///   6. Load employee shift intervals for overlap detection (one query)
///   7. Categorize models (pre-query scan areas + claims for fast/slow path split)
///   8. Weekly processing (WeeklyScheduleService)
///      - For each day 0..AdvanceDays
///      - Day-of-week filter, multi-week check, duplicate check, overlap check
///      - Fast path: bulk INSERT / Slow path: individual INSERT + claims + scan areas
///   9. Monthly processing (MonthlyScheduleService)
///      - For each of N months: calculate Nth weekday, duplicate check, overlap check, insert
///  10. Flush audit log + conflict entries to DB
///  11. Finalization
///      - Update lastrundate (weekly: NOW(), monthly: 1st-of-next-month)
///      - Finalize multi-week tracking (update Nextscheduledate)
///      - Cleanup audit tables (3-day retention)
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
    /// Run the complete scheduling job. This is the entry point called from Program.cs.
    /// </summary>
    /// <param name="scheduleDateTime">
    /// The base schedule date/time. Typically DateTime.Now, but can be overridden
    /// via command-line for testing.
    /// </param>
    /// <param name="ct">Cancellation token for graceful shutdown.</param>
    public async Task RunAsync(DateTime scheduleDateTime, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var sessionId = Guid.NewGuid().ToString();
        var startTime = DateTime.Now;

        // Accumulators for run summary (master record for portal drill-down)
        int orphanedDeleted = 0, resetDeleted = 0;
        int weeklyModelsLoaded = 0, recordsConsidered = 0, shiftsCreated = 0, shiftsSkipped = 0;
        int auditEntriesCount = 0, conflictsCount = 0;
        string runStatus = "Completed";
        string? runError = null;

        _logger.LogInformation(
            "========================================================================");
        _logger.LogInformation(
            "JMScheduler job starting. SessionId={SessionId}, ScheduleDate={Date:yyyy-MM-dd HH:mm}, " +
            "AdvanceDays={AdvanceDays}, MonthlyMonthsAhead={Monthly}",
            sessionId, scheduleDateTime, _config.AdvanceDays, _config.MonthlyMonthsAhead);
        _logger.LogInformation(
            "========================================================================");

        // ================================================================
        // STEP 0: Concurrency guard (StartEvent)
        // ================================================================
        var canRun = await _repo.StartJobSessionAsync(sessionId, startTime, "ShiftSchedular", ct);
        if (!canRun)
        {
            _logger.LogWarning(
                "StartEvent returned 0 — another instance may already be running. Exiting.");
            return;
        }

            await _repo.LogJobTrackingAsync("C# SchedulerJob: started", ct);

        // Insert run summary row (RunId = primary key for portal drill-down)
        await _repo.InsertRunSummaryAsync(sessionId, startTime, ct);

        try
        {
            // ================================================================
            // STEP 1: Ensure audit/conflict tables exist
            // ================================================================
            await _repo.EnsureAuditTablesAsync(ct);

            // ================================================================
            // STEP 2: Cleanup phase
            // ================================================================
            (orphanedDeleted, resetDeleted) = await _cleanupService.RunAsync(ct);
            await _repo.LogJobTrackingAsync(
                $"C# cleanup: orphaned={orphanedDeleted}, reset={resetDeleted}", ct);

            // ================================================================
            // STEP 3: Load weekly models
            // ================================================================
            var phaseSw = Stopwatch.StartNew();
            var weeklyModels = await _repo.LoadWeeklyModelsAsync(scheduleDateTime, ct);
            phaseSw.Stop();
            weeklyModelsLoaded = weeklyModels.Count;
            _logger.LogInformation("Model loading completed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // ================================================================
            // STEP 4: Load existing shift keys for duplicate detection
            // Covers the full date range of both weekly and monthly windows.
            // ================================================================
            phaseSw.Restart();
            var endDate = scheduleDateTime.AddDays(_config.AdvanceDays + 1);

            // Monthly can go up to ~3 months ahead, extend the end date
            var monthlyEndDate = scheduleDateTime.AddMonths(_config.MonthlyMonthsAhead).AddDays(1);
            if (monthlyEndDate > endDate)
                endDate = monthlyEndDate;

            var existingKeys = await _repo.LoadExistingShiftKeysAsync(scheduleDateTime, endDate, ct);
            phaseSw.Stop();
            _logger.LogInformation("Shift key loading completed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // ================================================================
            // STEP 4.5: Load employee shift intervals for overlap detection
            // ================================================================
            phaseSw.Restart();
            var overlapDetector = new OverlapDetector();
            var intervals = await _repo.LoadEmployeeShiftIntervalsAsync(scheduleDateTime, endDate, ct);
            overlapDetector.Load(intervals);
            phaseSw.Stop();
            _logger.LogInformation(
                "Overlap detector loaded in {Elapsed:F1}s: {Intervals} intervals for {Employees} employees",
                phaseSw.Elapsed.TotalSeconds, overlapDetector.TotalIntervals, overlapDetector.UniqueEmployees);

            // ================================================================
            // STEP 5: Pre-load model categorization for fast/slow path
            // ================================================================
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

            // ================================================================
            // Audit + conflict accumulators (flushed to DB after processing)
            // ================================================================
            var auditEntries = new List<ShiftAuditEntry>();
            var conflicts = new List<ShiftConflict>();

            // ================================================================
            // STEP 6: Weekly processing
            // ================================================================
            WeeklyScheduleService.WeeklyResult? weeklyResult = null;

            if (weeklyModels.Count > 0)
            {
                weeklyResult = await _weeklyService.ProcessAsync(
                    weeklyModels, scheduleDateTime, existingKeys,
                    modelsWithScanAreas, modelsWithClaims, multiWeekTracking,
                    sessionId, startTime, overlapDetector, auditEntries, conflicts, ct);

                await _repo.LogJobTrackingAsync(
                    $"C# weekly: created={weeklyResult.TotalShiftsCreated}, " +
                    $"fast={weeklyResult.FastPathInserted}, slow={weeklyResult.SlowPathInserted}, " +
                    $"dupes={weeklyResult.DuplicatesSkipped}, overlaps={weeklyResult.OverlapsBlocked}", ct);
            }
            else
            {
                _logger.LogInformation("No weekly models to process");
            }

            // ================================================================
            // STEP 7: Monthly processing (always runs, not just Saturdays)
            // The original SP only ran on Saturdays, but that was an arbitrary
            // limitation. Running daily ensures shifts are created promptly.
            // ================================================================
            var monthlyResult = await _monthlyService.ProcessAsync(
                scheduleDateTime, existingKeys, modelsWithScanAreas,
                sessionId, startTime, overlapDetector, auditEntries, conflicts, ct);

            await _repo.LogJobTrackingAsync(
                $"C# monthly: created={monthlyResult.TotalShiftsCreated}, " +
                $"dupes={monthlyResult.DuplicatesSkipped}, overlaps={monthlyResult.OverlapsBlocked}", ct);

            // ================================================================
            // STEP 8: Flush audit log and conflict entries to DB
            // ================================================================
            phaseSw.Restart();
            _logger.LogInformation(
                "Flushing audit data: {AuditCount} audit entries, {ConflictCount} conflicts",
                auditEntries.Count, conflicts.Count);

            await _repo.BulkInsertAuditLogAsync(auditEntries, ct);
            await _repo.BulkInsertConflictsAsync(conflicts, ct);
            phaseSw.Stop();
            _logger.LogInformation("Audit data flushed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // ================================================================
            // STEP 9: Finalization
            // ================================================================
            phaseSw.Restart();

            // 9a. Update lastrundate for ALL loaded weekly models (set to NOW)
            // Must update every model that was loaded, not just ones that created shifts.
            // Otherwise models with all-duplicate shifts never get their lastrundate updated
            // and keep getting loaded every day unnecessarily.
            if (weeklyModels.Count > 0)
            {
                var allWeeklyModelIds = weeklyModels.Select(m => m.Id);
                await _repo.UpdateWeeklyLastRunDatesAsync(allWeeklyModelIds, ct);
            }

            // 9b. Finalize multi-week tracking
            if (weeklyResult?.MultiWeekModelsWithChanges.Count > 0)
            {
                foreach (int modelId in weeklyResult.MultiWeekModelsWithChanges)
                {
                    // Find the latest schedule date generated for this model
                    var lastShiftDate = await _repo.GetLastShiftDateForModelAsync(modelId, ct);
                    if (lastShiftDate.HasValue)
                    {
                        await _repo.FinalizeMultiWeekTrackingAsync(modelId, lastShiftDate.Value, ct);
                    }
                }

                _logger.LogInformation("Finalized multi-week tracking for {Count} models",
                    weeklyResult.MultiWeekModelsWithChanges.Count);
            }

            // 9c. Update lastrundate for ALL loaded monthly models (set to 1st-of-next-month per month window)
            // Must update every model that was loaded, not just ones that created shifts.
            // Otherwise models with all-duplicate shifts never get their lastrundate updated
            // and keep getting loaded every day unnecessarily.
            foreach (var (monthStart, modelIds) in monthlyResult.AllLoadedModelsByMonth)
            {
                await _repo.UpdateMonthlyLastRunDatesAsync(modelIds, monthStart, ct);
            }

            // 9d. Cleanup audit tables (3-day retention)
            await _repo.CleanupAuditTablesAsync(_config.AuditRetentionDays, ct);
            _logger.LogInformation("Audit table cleanup completed (retention: {Days} days)",
                _config.AuditRetentionDays);

            phaseSw.Stop();
            _logger.LogInformation("Finalization completed in {Elapsed:F1}s", phaseSw.Elapsed.TotalSeconds);

            // ================================================================
            // Summary and run master record (for portal drill-down by RunId)
            // ================================================================
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
            // ================================================================
            // STEP 10: Update run summary (master) and complete job session
            // ================================================================
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
    }
}
