using System.Diagnostics;
using Microsoft.Extensions.Logging;
using JMScheduler.Job.Configuration;
using JMScheduler.Job.Data;
using JMScheduler.Job.Infrastructure;
using JMScheduler.Job.Models;

namespace JMScheduler.Job.Services;

/// <summary>
/// Processes weekly (RecurringType=0) and multi-week (RecurringOn > 1) schedule models.
/// Replaces ProcessScheduleModal stored procedure (650 lines).
///
/// Execution flow per model per day:
///   1. Check day-of-week flag (in-memory)
///   2. Multi-week check via MultiWeekDateCalculator (in-memory, no DB)
///   3. Duplicate check via HashSet (in-memory, O(1))
///   4. Overlap check via OverlapDetector (in-memory) — blocks conflicting shifts at different locations
///   5. ScheduleType=1 (OpenWithAllClaim) uses modal-aware key for dedup (allows different models, blocks same-model re-runs)
///   6. Split into fast path (bulk insert) or slow path (individual + claims + scan areas)
///
/// Behavioral notes (must match original SP exactly):
///   - Note text: "Scheduled Event"
///   - Claim copy: YES — copies employeescheduleshiftmodelclaim → employeescheduleshiftclaim
///   - Group handling: CLONES existing groupschedule row (copies Client_id, flags)
///   - lastrundate: set to NOW() after processing
///   - Scan areas: calls ProcessRecurring_ScheduleScanArea for non-group shifts only
/// </summary>
public sealed class WeeklyScheduleService
{
    private const string NoteText = "Scheduled Event";

    private readonly ScheduleRepository _repo;
    private readonly MultiWeekDateCalculator _multiWeekCalc;
    private readonly DeadlockRetryHandler _retryHandler;
    private readonly SchedulerConfig _config;
    private readonly ILogger<WeeklyScheduleService> _logger;

    public WeeklyScheduleService(
        ScheduleRepository repo,
        MultiWeekDateCalculator multiWeekCalc,
        DeadlockRetryHandler retryHandler,
        SchedulerConfig config,
        ILogger<WeeklyScheduleService> logger)
    {
        _repo          = repo;
        _multiWeekCalc = multiWeekCalc;
        _retryHandler  = retryHandler;
        _config        = config;
        _logger        = logger;
    }

    /// <summary>
    /// Result of weekly processing — returned to the orchestrator for finalization.
    /// </summary>
    public sealed class WeeklyResult
    {
        public int TotalShiftsCreated { get; set; }
        public int FastPathInserted { get; set; }
        public int SlowPathInserted { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int OverlapsBlocked { get; set; }
        public HashSet<int> ProcessedModelIds { get; } = new();
        public HashSet<int> MultiWeekModelsWithChanges { get; } = new();
    }

    /// <summary>
    /// Process all weekly/multi-week models across all days in the advance window.
    /// </summary>
    /// <param name="models">All weekly models (RecurringType=0) already loaded.</param>
    /// <param name="scheduleDateTime">The base schedule date (typically today).</param>
    /// <param name="existingKeys">Pre-loaded HashSet of existing shift keys for duplicate check.</param>
    /// <param name="modelsWithScanAreas">Set of model IDs that have scan area templates.</param>
    /// <param name="modelsWithClaims">Set of model IDs that have claim templates.</param>
    /// <param name="multiWeekTracking">Tracking data for multi-week models.</param>
    /// <param name="runId">Current job run session ID (for audit logging).</param>
    /// <param name="runDate">Current job run start time (for audit logging).</param>
    /// <param name="overlapDetector">In-memory overlap detector (loaded with existing intervals).</param>
    /// <param name="auditEntries">Accumulator list for audit log entries (flushed by orchestrator).</param>
    /// <param name="conflicts">Accumulator list for overlap conflicts (flushed by orchestrator).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WeeklyResult> ProcessAsync(
        List<ScheduleModel> models,
        DateTime scheduleDateTime,
        HashSet<string> existingKeys,
        HashSet<string> existingModalKeys,
        HashSet<int> modelsWithScanAreas,
        HashSet<int> modelsWithClaims,
        Dictionary<int, NextRunStatus> multiWeekTracking,
        string runId,
        DateTime runDate,
        OverlapDetector overlapDetector,
        List<ShiftAuditEntry> auditEntries,
        List<ShiftConflict> conflicts,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new WeeklyResult();

        _logger.LogInformation(
            "=== Weekly processing starting: {ModelCount} models, {AdvanceDays} days ===",
            models.Count, _config.AdvanceDays);

        // Pre-compute multi-week valid dates for all multi-week models (in-memory)
        var multiWeekValidDates = await PrecomputeMultiWeekDatesAsync(
            models.Where(m => m.IsMultiWeek).ToList(), multiWeekTracking, ct);

        // Process each day in the advance window
        for (int dayOffset = 0; dayOffset <= _config.AdvanceDays; dayOffset++)
        {
            var targetDate = scheduleDateTime.AddDays(dayOffset);
            var dayOfWeek = targetDate.DayOfWeek;

            // Filter models eligible for this day
            var eligibleModels = models
                .Where(m => m.IsScheduledForDay(dayOfWeek))
                .Where(m => m.StartDate.Date <= DateTime.Now.Date)
                .Where(m => m.HasNoEndDate || m.EndDate.Date >= targetDate.Date)
                .ToList();

            if (eligibleModels.Count == 0) continue;

            // Further filter multi-week models — only include if this date is valid
            eligibleModels = eligibleModels
                .Where(m =>
                {
                    if (!m.IsMultiWeek) return true; // Weekly models always pass
                    return multiWeekValidDates.TryGetValue(m.Id, out var validDates)
                           && validDates.Contains(targetDate.Date);
                })
                .ToList();

            if (eligibleModels.Count == 0) continue;

            // Four-category split for maximum throughput:
            //   1. fastPathShifts:  no scan areas, no claims, no groups → bulk INSERT only
            //   2. scanAreaShifts:  has scan areas, NO claims, NO groups → bulk INSERT + bulk scan area copy
            //   3. claimsShifts:    has claims (±scan areas), NO groups → bulk INSERT + bulk claims copy + bulk scan copy
            //   4. groupModels:     has groups → individual INSERT (needs LAST_INSERT_ID for group clone)
            var fastPathShifts = new List<ScheduleShift>();
            var scanAreaShifts = new List<ScheduleShift>();
            var scanAreaModelIds = new List<int>();
            var claimsShifts = new List<ScheduleShift>();
            var claimsModelIds = new List<int>();
            var claimsWithScanAreaIds = new List<int>(); // subset that also has scan areas
            var groupModels = new List<ScheduleModel>();

            foreach (var model in eligibleModels)
            {
                bool hasScanAreas = modelsWithScanAreas.Contains(model.Id);
                bool hasClaims = modelsWithClaims.Contains(model.Id);
                bool hasGroup = model.HasGroupSchedule;

                // Build the shift to check for duplicates
                var shift = ScheduleShift.FromModel(model, targetDate, NoteText);
                var key = shift.GetDuplicateKey();
                var modalKey = shift.GetModalDuplicateKey();
                string pattern = ShiftAuditEntry.BuildRecurringPattern(model);

                // Duplicate check:
                //   ScheduleType=1 (OpenWithAllClaim): uses modal-aware key so different models
                //     can create shifts for the same slot, but the SAME model won't duplicate
                //     across runs. This fixes the "12 copies per day" bug.
                //   All other types: standard key (Client|Employee|DateTimeIn|DateTimeOut).
                bool isDuplicate = model.ScheduleType == 1
                    ? existingModalKeys.Contains(modalKey)
                    : existingKeys.Contains(key);

                if (isDuplicate)
                {
                    result.DuplicatesSkipped++;
                    auditEntries.Add(ShiftAuditEntry.Duplicate(runId, runDate, shift, model, "Weekly", pattern));
                    continue;
                }

                // --- Overlap check (only for assigned employees at different locations) ---
                var conflict = overlapDetector.CheckOverlap(
                    model.EmployeeId, model.Client_id, shift.DateTimeIn, shift.DateTimeOut);

                if (conflict.HasValue)
                {
                    var c = conflict.Value;
                    string desc = $"Overlaps with ShiftId {c.ShiftId} at ClientId {c.ClientId} " +
                                  $"({c.Start:HH:mm}-{c.End:HH:mm})";

                    result.OverlapsBlocked++;
                    auditEntries.Add(ShiftAuditEntry.Overlap(runId, runDate, shift, model, "Weekly", pattern, desc));
                    conflicts.Add(new ShiftConflict
                    {
                        RunId              = runId,
                        ModalId            = model.Id,
                        EmployeeId         = model.EmployeeId,
                        ClientId           = model.Client_id,
                        DateTimeIn         = shift.DateTimeIn,
                        DateTimeOut        = shift.DateTimeOut,
                        ConflictingShiftId = c.ShiftId,
                        ConflictingModalId = c.ModalId,
                        ConflictingClientId = c.ClientId,
                        ConflictDateTimeIn  = c.Start,
                        ConflictDateTimeOut = c.End
                    });
                    continue;
                }

                // Add keys to prevent intra-run duplicates
                existingKeys.Add(key);
                existingModalKeys.Add(modalKey);
                result.ProcessedModelIds.Add(model.Id);

                if (model.IsMultiWeek)
                    result.MultiWeekModelsWithChanges.Add(model.Id);

                // Register this shift in the overlap detector for subsequent checks
                overlapDetector.RegisterShift(
                    model.EmployeeId, model.Client_id, shift.DateTimeIn, shift.DateTimeOut, model.Id);

                // Log audit entry as Created
                auditEntries.Add(ShiftAuditEntry.Created(runId, runDate, shift, model, "Weekly", pattern));

                if (hasGroup)
                {
                    // Group path: needs LAST_INSERT_ID for group clone — truly individual
                    groupModels.Add(model);
                }
                else if (hasClaims)
                {
                    // Claims path: bulk insert + bulk claims copy (+ bulk scan if needed)
                    claimsShifts.Add(shift);
                    claimsModelIds.Add(model.Id);
                    if (hasScanAreas) claimsWithScanAreaIds.Add(model.Id);
                }
                else if (hasScanAreas)
                {
                    // Scan-area-only path: bulk insert + bulk scan area copy
                    scanAreaShifts.Add(shift);
                    scanAreaModelIds.Add(model.Id);
                }
                else
                {
                    // Fast path: pure bulk insert, no post-processing
                    fastPathShifts.Add(shift);
                }
            }

            // 1. Fast path: bulk insert (no post-processing)
            if (fastPathShifts.Count > 0)
            {
                int inserted = await _repo.BulkInsertShiftsAsync(fastPathShifts, ct);
                result.FastPathInserted += inserted;
                result.TotalShiftsCreated += inserted;
            }

            // 2. Scan-area-only path: bulk insert + bulk scan area copy
            if (scanAreaShifts.Count > 0)
            {
                int inserted = await _repo.BulkInsertShiftsAsync(scanAreaShifts, ct);
                result.FastPathInserted += inserted;
                result.TotalShiftsCreated += inserted;

                await _repo.BulkCopyScanAreasAsync(scanAreaModelIds, targetDate, ct);
            }

            // 3. Claims path: bulk insert + bulk claims copy + optional bulk scan area copy
            if (claimsShifts.Count > 0)
            {
                int inserted = await _repo.BulkInsertShiftsAsync(claimsShifts, ct);
                result.FastPathInserted += inserted;
                result.TotalShiftsCreated += inserted;

                // Bulk copy claims for all inserted shifts
                await _repo.BulkCopyClaimsAsync(claimsModelIds, targetDate, ct);

                // Bulk copy scan areas for claim models that also have scan areas
                if (claimsWithScanAreaIds.Count > 0)
                {
                    await _repo.BulkCopyScanAreasAsync(claimsWithScanAreaIds, targetDate, ct);
                }
            }

            // 4. Group path: individual INSERT (needs group clone — very few models)
            //    Deduplicate by GroupScheduleId: multiple models may share a group,
            //    but InsertGroupShiftsAsync already inserts ALL models in the group.
            //    Without dedup, each model triggers a full group insertion → Nx duplication.
            if (groupModels.Count > 0)
            {
                var uniqueGroupModels = groupModels
                    .GroupBy(m => m.GroupScheduleId)
                    .Select(g => g.First())
                    .ToList();

                int slowInserted = await ProcessSlowPathAsync(
                    uniqueGroupModels, targetDate, modelsWithScanAreas, modelsWithClaims,
                    existingKeys, existingModalKeys, ct);
                result.SlowPathInserted += slowInserted;
                result.TotalShiftsCreated += slowInserted;
            }

            if ((dayOffset + 1) % 5 == 0 || dayOffset == _config.AdvanceDays || dayOffset == 0)
            {
                _logger.LogInformation(
                    "Weekly day {DayOffset}/{Total}: {Date:yyyy-MM-dd} ({DayOfWeek}) — " +
                    "eligible={Eligible}, fast={Fast}, scanArea={ScanArea}, claims={Claims}, groups={Groups}",
                    dayOffset, _config.AdvanceDays, targetDate, dayOfWeek,
                    eligibleModels.Count, fastPathShifts.Count, scanAreaShifts.Count,
                    claimsShifts.Count, groupModels.Count);
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "=== Weekly processing completed in {Elapsed:F1}s. " +
            "Created={Total} (fast={Fast}, slow={Slow}), Duplicates={Dupes}, Overlaps={Overlaps}, Models={Models} ===",
            sw.Elapsed.TotalSeconds, result.TotalShiftsCreated,
            result.FastPathInserted, result.SlowPathInserted,
            result.DuplicatesSkipped, result.OverlapsBlocked, result.ProcessedModelIds.Count);

        return result;
    }

    /// <summary>
    /// Pre-compute valid schedule dates for all multi-week models.
    /// Replaces hundreds of thousands of SpanClientScheduleShift DB calls with pure C# math.
    ///
    /// Optimization: uses 2 bulk queries to fetch last shift dates and last history dates
    /// for ALL multi-week models at once, instead of 2 queries per model (562 → 2 queries).
    /// </summary>
    private async Task<Dictionary<int, HashSet<DateTime>>> PrecomputeMultiWeekDatesAsync(
        List<ScheduleModel> multiWeekModels,
        Dictionary<int, NextRunStatus> tracking,
        CancellationToken ct)
    {
        var result = new Dictionary<int, HashSet<DateTime>>();

        if (multiWeekModels.Count == 0) return result;

        _logger.LogInformation("Pre-computing multi-week dates for {Count} models (bulk query)", multiWeekModels.Count);

        // Bulk fetch: 2 queries instead of 2*N queries
        var modelIds = multiWeekModels.Select(m => m.Id).ToList();
        var lastShiftDates = await _repo.GetLastShiftDatesForModelsAsync(modelIds, ct);
        var lastHistoryDates = await _repo.GetLastHistoryDatesForModelsAsync(modelIds, ct);

        _logger.LogInformation(
            "Bulk loaded multi-week data: {ShiftDates} shift dates, {HistoryDates} history dates",
            lastShiftDates.Count, lastHistoryDates.Count);

        // Now compute valid dates in-memory (no more DB calls)
        foreach (var model in multiWeekModels)
        {
            tracking.TryGetValue(model.Id, out var trackingStatus);

            DateTime? lastShiftDate = lastShiftDates.TryGetValue(model.Id, out var sd) ? sd : null;
            DateTime? lastHistoryDate = lastHistoryDates.TryGetValue(model.Id, out var hd) ? hd : null;

            var (anchorDate, restrictionDate) = _multiWeekCalc.ResolveAnchorAndRestriction(
                model, trackingStatus, lastShiftDate, lastHistoryDate);

            var validDates = _multiWeekCalc.CalculateValidDates(
                model, anchorDate, restrictionDate, _config.AdvanceDays);

            result[model.Id] = validDates;

            _logger.LogDebug(
                "Multi-week model {ModelId}: anchor={Anchor:yyyy-MM-dd}, restriction={Restriction:yyyy-MM-dd}, " +
                "validDates={Count}",
                model.Id, anchorDate, restrictionDate, validDates.Count);
        }

        return result;
    }

    /// <summary>
    /// Process slow-path models: individual INSERT + claim copy + scan area SP.
    /// Each model gets its own connection for LAST_INSERT_ID() accuracy.
    /// </summary>
    private async Task<int> ProcessSlowPathAsync(
        List<ScheduleModel> models,
        DateTime targetDate,
        HashSet<int> modelsWithScanAreas,
        HashSet<int> modelsWithClaims,
        HashSet<string> existingKeys,
        HashSet<string> existingModalKeys,
        CancellationToken ct)
    {
        int totalInserted = 0;

        foreach (var model in models)
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _repo.CreateConnectionAsync(ct);

                if (model.HasGroupSchedule)
                {
                    // Group path: clone group row, then insert non-duplicate models in the group
                    int newGroupId = await _repo.CloneGroupScheduleAsync(conn, model.GroupScheduleId, ct);
                    var insertedIds = await _repo.InsertGroupShiftsAsync(
                        conn, model.GroupScheduleId, newGroupId, targetDate, NoteText,
                        existingKeys, existingModalKeys, ct);
                    totalInserted += insertedIds.Count;
                }
                else
                {
                    // Non-group slow path: individual insert, then claims + scan areas
                    var shift = ScheduleShift.FromModel(model, targetDate, NoteText);
                    long shiftId = await _repo.InsertShiftAndGetIdAsync(conn, shift, ct);
                    totalInserted++;

                    // Copy claims (weekly behavior — monthly does NOT do this)
                    if (modelsWithClaims.Contains(model.Id))
                    {
                        await _repo.CopyModelClaimsToShiftAsync(conn, model.Id, shiftId, ct);
                    }

                    // Call scan area SP
                    if (modelsWithScanAreas.Contains(model.Id))
                    {
                        await _repo.CallProcessRecurringScanAreaAsync(conn, model.Id, shiftId, ct);
                    }
                }
            }, $"SlowPath_Model_{model.Id}", ct);
        }

        return totalInserted;
    }
}
