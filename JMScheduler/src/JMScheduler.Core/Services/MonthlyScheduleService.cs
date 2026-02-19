using System.Diagnostics;
using Microsoft.Extensions.Logging;
using JMScheduler.Core.Configuration;
using JMScheduler.Core.Data;
using JMScheduler.Core.Infrastructure;
using JMScheduler.Core.Models;

namespace JMScheduler.Core.Services;

/// <summary>
/// Processes monthly recurring schedule models (RecurringType = 1).
/// Replaces ProcessScheduleModal_Monthly stored procedure (257 lines).
///
/// Monthly scheduling calculates the Nth occurrence of a weekday in a month.
/// MonthlyRecurringType maps to:
///   0 = 1st occurrence (e.g., 1st Monday)
///   1 = 2nd occurrence
///   2 = 3rd occurrence
///   3 = 4th occurrence (with overflow → falls back to last occurrence)
///
/// Processes current month + 2 months ahead = 3 months total.
///
/// Behavioral notes (differences from weekly, must preserve exactly):
///   - Note text: "Schedule Event Monthly" (note: no 'd' at end)
///   - Claim copy: NO — monthly does NOT copy claims
///   - Group handling: INSERTS NEW groupschedule row (IsEmployeeSchedule=1, IsClientSchedule=0)
///     as opposed to weekly which CLONES the existing row
///   - lastrundate: set to 1st of next month (not NOW())
///   - Scan areas: calls ProcessRecurring_ScheduleScanArea for non-group shifts
///   - Advance window: Always 3 months (current + 2), NOT based on AdvanceDays config
/// </summary>
public sealed class MonthlyScheduleService
{
    private const string NoteText = "Schedule Event Monthly";

    private readonly ScheduleRepository _repo;
    private readonly DeadlockRetryHandler _retryHandler;
    private readonly SchedulerConfig _config;
    private readonly ILogger<MonthlyScheduleService> _logger;

    public MonthlyScheduleService(
        ScheduleRepository repo,
        DeadlockRetryHandler retryHandler,
        SchedulerConfig config,
        ILogger<MonthlyScheduleService> logger)
    {
        _repo          = repo;
        _retryHandler  = retryHandler;
        _config        = config;
        _logger        = logger;
    }

    /// <summary>
    /// Result of monthly processing — returned to the orchestrator for finalization.
    /// </summary>
    public sealed class MonthlyResult
    {
        public int TotalShiftsCreated { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int OverlapsBlocked { get; set; }
        public int DateBeforeStartSkipped { get; set; }

        /// <summary>
        /// Model IDs that were processed, grouped by their month start date for lastrundate update.
        /// Key = first-of-month date used for lastrundate calculation.
        /// </summary>
        public Dictionary<DateTime, HashSet<int>> ProcessedModelsByMonth { get; } = new();

        /// <summary>
        /// ALL model IDs that were loaded for each month, regardless of whether shifts were created.
        /// Used for lastrundate finalization — every loaded model should have its lastrundate
        /// advanced so it isn't re-loaded on the next run.
        /// Key = first-of-month date used for lastrundate calculation.
        /// </summary>
        public Dictionary<DateTime, HashSet<int>> AllLoadedModelsByMonth { get; } = new();
    }

    /// <summary>
    /// Process all monthly models across the month window.
    /// </summary>
    /// <param name="scheduleDate">The base date.</param>
    /// <param name="existingKeys">Pre-loaded HashSet for duplicate detection.</param>
    /// <param name="modelsWithScanAreas">Models that need scan area SP call.</param>
    /// <param name="runId">Current job run session ID (for audit logging).</param>
    /// <param name="runDate">Current job run start time (for audit logging).</param>
    /// <param name="overlapDetector">In-memory overlap detector.</param>
    /// <param name="auditEntries">Accumulator list for audit log entries.</param>
    /// <param name="conflicts">Accumulator list for overlap conflicts.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MonthlyResult> ProcessAsync(
        DateTime scheduleDate,
        HashSet<string> existingKeys,
        HashSet<string> existingModalKeys,
        HashSet<int> modelsWithScanAreas,
        string runId,
        DateTime runDate,
        OverlapDetector overlapDetector,
        List<ShiftAuditEntry> auditEntries,
        List<ShiftConflict> conflicts,
        int effectiveMonthlyMonths,
        int companyId,
        int modelId,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new MonthlyResult();

        _logger.LogInformation("=== Monthly processing starting for {MonthsAhead} months ===",
            effectiveMonthlyMonths);

        // Process each month in the window
        for (int monthOffset = 0; monthOffset < effectiveMonthlyMonths; monthOffset++)
        {
            var targetMonth = scheduleDate.AddMonths(monthOffset);
            _logger.LogInformation("Processing monthly models for {Month:yyyy-MM}", targetMonth);

            var models = await _repo.LoadMonthlyModelsAsync(targetMonth, ct, companyId, modelId);

            if (models.Count == 0)
            {
                _logger.LogInformation("No eligible monthly models for {Month:yyyy-MM}", targetMonth);
                continue;
            }

            // Calculate the first day of the month
            var monthStart = new DateTime(targetMonth.Year, targetMonth.Month, 1);

            // Track ALL loaded models for this month (for lastrundate finalization)
            if (!result.AllLoadedModelsByMonth.TryGetValue(monthStart, out var allMonthModelIds))
            {
                allMonthModelIds = new HashSet<int>();
                result.AllLoadedModelsByMonth[monthStart] = allMonthModelIds;
            }
            foreach (var m in models)
                allMonthModelIds.Add(m.Id);

            // Three-category split (same optimization as weekly):
            //   1. bulkShifts: no scan areas, no groups → bulk INSERT
            //   2. scanAreaShifts: has scan areas, no groups → bulk INSERT + bulk scan area copy
            //   3. groupModels: has groups → individual INSERT (needs LAST_INSERT_ID for group clone)
            var bulkShifts = new List<ScheduleShift>();
            var scanAreaShifts = new List<ScheduleShift>();
            var scanAreaModelIds = new List<int>();
            var groupModels = new List<(ScheduleModel Model, DateTime TargetDate)>();

            foreach (var model in models)
            {
                // Determine which weekday this model is scheduled for
                DayOfWeek? scheduledDay = GetScheduledDayOfWeek(model);
                if (scheduledDay == null)
                {
                    _logger.LogWarning("Monthly model {ModelId} has no day-of-week flag set, skipping", model.Id);
                    continue;
                }

                // Calculate the Nth occurrence of that weekday in this month
                DateTime? targetDate = CalculateNthWeekdayOfMonth(
                    monthStart, scheduledDay.Value, model.MonthlyRecurringType);

                if (targetDate == null)
                {
                    _logger.LogWarning(
                        "Could not calculate target date for model {ModelId}, month={Month:yyyy-MM}",
                        model.Id, targetMonth);
                    continue;
                }

                // Check: target date must be >= model's start date
                // Mirrors: MonthlySchedular.sql line 130 — if(date(P_datetimein) >= date(p_recurringstartdate))
                if (targetDate.Value.Date < model.StartDate.Date)
                {
                    result.DateBeforeStartSkipped++;
                    continue;
                }

                // Build shift for duplicate check
                var shift = ScheduleShift.FromModel(model, targetDate.Value, NoteText);
                var key = shift.GetDuplicateKey();
                var modalKey = shift.GetModalDuplicateKey();
                string pattern = ShiftAuditEntry.BuildRecurringPattern(model);

                // Same dual-key dedup as weekly:
                //   ScheduleType=1 → modal-aware key (allow different models, block same model re-runs)
                //   All others → standard key
                bool isDuplicate = model.ScheduleType == 1
                    ? existingModalKeys.Contains(modalKey)
                    : existingKeys.Contains(key);

                if (isDuplicate)
                {
                    result.DuplicatesSkipped++;
                    auditEntries.Add(ShiftAuditEntry.Duplicate(runId, runDate, shift, model, "Monthly", pattern));
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
                    auditEntries.Add(ShiftAuditEntry.Overlap(runId, runDate, shift, model, "Monthly", pattern, desc));
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

                existingKeys.Add(key);
                existingModalKeys.Add(modalKey);

                // Register this shift in the overlap detector for subsequent checks
                overlapDetector.RegisterShift(
                    model.EmployeeId, model.Client_id, shift.DateTimeIn, shift.DateTimeOut, model.Id);

                // Log audit entry as Created
                auditEntries.Add(ShiftAuditEntry.Created(runId, runDate, shift, model, "Monthly", pattern));

                // Track for lastrundate update — group by month start
                if (!result.ProcessedModelsByMonth.TryGetValue(monthStart, out var monthModelIds))
                {
                    monthModelIds = new HashSet<int>();
                    result.ProcessedModelsByMonth[monthStart] = monthModelIds;
                }
                monthModelIds.Add(model.Id);

                // Categorize
                if (model.HasGroupSchedule)
                {
                    groupModels.Add((model, targetDate.Value));
                }
                else if (modelsWithScanAreas.Contains(model.Id))
                {
                    scanAreaShifts.Add(shift);
                    scanAreaModelIds.Add(model.Id);
                }
                else
                {
                    bulkShifts.Add(shift);
                }
            }

            // Bulk insert non-scan-area, non-group shifts
            if (bulkShifts.Count > 0)
            {
                int inserted = await _repo.BulkInsertShiftsAsync(bulkShifts, ct);
                result.TotalShiftsCreated += inserted;
            }

            // Bulk insert scan-area shifts + bulk copy scan areas
            if (scanAreaShifts.Count > 0)
            {
                int inserted = await _repo.BulkInsertShiftsAsync(scanAreaShifts, ct);
                result.TotalShiftsCreated += inserted;

                // The scan area shifts may have different target dates within the month,
                // so we group by target date for the bulk copy
                var dateGroups = scanAreaShifts
                    .Select((s, i) => (Shift: s, ModelId: scanAreaModelIds[i]))
                    .GroupBy(x => x.Shift.DateTimeIn.Date);
                foreach (var dateGroup in dateGroups)
                {
                    var ids = dateGroup.Select(x => x.ModelId).ToList();
                    await _repo.BulkCopyScanAreasAsync(ids, dateGroup.Key, ct);
                }
            }

            // Group models: individual insert (needs group clone + LAST_INSERT_ID)
            // Deduplicate by GroupScheduleId: multiple models may share a group,
            // but InsertGroupShiftsAsync already inserts ALL models in the group.
            var uniqueGroupModels = groupModels
                .GroupBy(g => g.Model.GroupScheduleId)
                .Select(g => g.First())
                .ToList();

            foreach (var (model, targetDate) in uniqueGroupModels)
            {
                int inserted = await InsertMonthlyShiftAsync(
                    model, targetDate, modelsWithScanAreas,
                    existingKeys, existingModalKeys, ct);
                result.TotalShiftsCreated += inserted;
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "=== Monthly processing completed in {Elapsed:F1}s. " +
            "Created={Total}, Duplicates={Dupes}, Overlaps={Overlaps}, BeforeStart={Before} ===",
            sw.Elapsed.TotalSeconds, result.TotalShiftsCreated,
            result.DuplicatesSkipped, result.OverlapsBlocked, result.DateBeforeStartSkipped);

        return result;
    }

    /// <summary>
    /// Insert a monthly shift — handles both group and non-group models.
    /// Returns the number of shifts inserted.
    /// </summary>
    private async Task<int> InsertMonthlyShiftAsync(
        ScheduleModel model,
        DateTime targetDate,
        HashSet<int> modelsWithScanAreas,
        HashSet<string> existingKeys,
        HashSet<string> existingModalKeys,
        CancellationToken ct)
    {
        int inserted = 0;

        await _retryHandler.ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await _repo.CreateConnectionAsync(ct);

            if (model.HasGroupSchedule)
            {
                // Monthly group: INSERT NEW groupschedule row (different from weekly which clones)
                int newGroupId = await _repo.InsertNewGroupScheduleAsync(conn, model.Client_id, ct);
                var insertedIds = await _repo.InsertMonthlyGroupShiftsAsync(
                    conn, model.GroupScheduleId, newGroupId, targetDate,
                    existingKeys, existingModalKeys, ct);
                inserted = insertedIds.Count;
            }
            else
            {
                // Non-group: individual insert + scan area (no claims for monthly)
                var shift = ScheduleShift.FromModel(model, targetDate, NoteText);
                long shiftId = await _repo.InsertShiftAndGetIdAsync(conn, shift, ct);
                inserted = 1;

                // Scan areas (monthly calls this for non-group shifts)
                if (modelsWithScanAreas.Contains(model.Id))
                {
                    await _repo.CallProcessRecurringScanAreaAsync(conn, model.Id, shiftId, ct);
                }
            }
        }, $"MonthlyInsert_Model_{model.Id}", ct);

        return inserted;
    }

    /// <summary>
    /// Calculate the Nth occurrence of a weekday in a month.
    ///
    /// Algorithm (mirrors MonthlySchedular.sql lines 90-116):
    ///   1. Find the first day of the month
    ///   2. Walk the first 7 days to find the first occurrence of the target weekday
    ///   3. Add (MonthlyRecurringType * 7) days to get the Nth occurrence
    ///   4. If the result falls in the next month, subtract 7 days (overflow → last occurrence)
    ///
    /// MonthlyRecurringType: 0=1st, 1=2nd, 2=3rd, 3=4th (with overflow protection)
    /// </summary>
    public static DateTime? CalculateNthWeekdayOfMonth(
        DateTime monthStart, DayOfWeek targetDay, int nthOccurrence)
    {
        // Find the first occurrence of the target weekday in the month
        DateTime firstOccurrence = monthStart;
        for (int i = 0; i < 7; i++)
        {
            if (monthStart.AddDays(i).DayOfWeek == targetDay)
            {
                firstOccurrence = monthStart.AddDays(i);
                break;
            }
        }

        // Calculate the Nth occurrence by adding (n * 7) days
        DateTime targetDate = firstOccurrence.AddDays(nthOccurrence * 7);

        // Overflow protection: if we've gone past the month, go back one week
        // This makes "4th Monday" become "last Monday" when there's no 5th Monday
        if (targetDate.Month != monthStart.Month)
        {
            targetDate = targetDate.AddDays(-7);
        }

        // Safety: should still be in the correct month after adjustment
        if (targetDate.Month != monthStart.Month)
            return null;

        return targetDate;
    }

    /// <summary>
    /// Determine which day of the week this monthly model is scheduled for.
    /// Monthly models typically have exactly one day flag set.
    /// Returns null if no day flag is set.
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
}
