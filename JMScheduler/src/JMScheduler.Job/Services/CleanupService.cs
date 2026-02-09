using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using JMScheduler.Job.Configuration;
using JMScheduler.Job.Infrastructure;

namespace JMScheduler.Job.Services;

/// <summary>
/// Handles the cleanup phase of the scheduler job.
/// Replaces the DELETE loops in CallProcessScheduleModal (schedular.sql lines 61-152)
/// and the ClientShiftModalEditable procedure.
///
/// Operations performed (in order):
///   1. Seed job_ClientscheduleShiftnextrunStatus for new multi-week models
///   2. Batch-delete orphaned shifts (ModalId references a deleted model)
///   3. Batch-delete shifts for reset/inactive models
///   4. Run ClientShiftModalEditable logic (reset anchor dates for edited multi-week models)
///   5. Clear IsModelReset flags on clientschedulemodel
///   6. Clean working tables (job_clientschedulemodel, temp data tables, prune history)
/// </summary>
public sealed class CleanupService
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly DeadlockRetryHandler _retryHandler;
    private readonly SchedulerConfig _config;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        DbConnectionFactory dbFactory,
        DeadlockRetryHandler retryHandler,
        SchedulerConfig config,
        ILogger<CleanupService> logger)
    {
        _dbFactory   = dbFactory;
        _retryHandler = retryHandler;
        _config      = config;
        _logger      = logger;
    }

    /// <summary>
    /// Run all cleanup operations. Returns total shifts deleted.
    /// </summary>
    public async Task<(int orphanedDeleted, int resetDeleted)> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("=== Cleanup phase starting ===");

        // Step 1: Seed multi-week tracking table for any new models
        await SeedMultiWeekTrackingAsync(ct);

        // Step 2: Delete orphaned shifts (model was deleted but shifts remain)
        int orphanedDeleted = await DeleteOrphanedShiftsAsync(ct);

        // Step 3: Delete shifts for reset/inactive models
        int resetDeleted = await DeleteResetInactiveShiftsAsync(ct);

        // Step 4: Reset anchor dates for edited multi-week models (ClientShiftModalEditable logic)
        await ResetEditedModelAnchorsAsync(ct);

        // Step 5: Clear IsModelReset flags
        await ClearModelResetFlagsAsync(ct);

        // Step 6: Clean working tables
        await CleanWorkingTablesAsync(ct);

        sw.Stop();
        _logger.LogInformation(
            "=== Cleanup phase completed in {Elapsed:F1}s. Orphaned={Orphaned}, Reset={Reset} ===",
            sw.Elapsed.TotalSeconds, orphanedDeleted, resetDeleted);

        return (orphanedDeleted, resetDeleted);
    }

    /// <summary>
    /// Seed job_ClientscheduleShiftnextrunStatus for multi-week models (recurringon > 1)
    /// that don't yet have a tracking row.
    /// Mirrors schedular.sql lines 40-47.
    /// </summary>
    private async Task SeedMultiWeekTrackingAsync(CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO job_ClientscheduleShiftnextrunStatus
            SELECT Id, employeeid, Client_id, startdate, 0, 0
            FROM   clientschedulemodel
            WHERE  isActive = 1
              AND  recurringon > 1
              AND  Id NOT IN (SELECT modal_id FROM job_ClientscheduleShiftnextrunStatus)";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        int seeded = await conn.ExecuteAsync(sql, commandTimeout: 120);

        if (seeded > 0)
            _logger.LogInformation("Seeded {Count} new multi-week models into tracking table", seeded);
    }

    /// <summary>
    /// Delete orphaned shifts where ModalId references a model that no longer exists.
    /// Uses LEFT JOIN (not NOT IN) and pre-computes IDs for minimal lock duration.
    /// </summary>
    private async Task<int> DeleteOrphanedShiftsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting orphaned shift deletion...");

        const string findOrphansSql = @"
            SELECT css.Id
            FROM   clientscheduleshift css
                   LEFT JOIN clientschedulemodel cm ON css.ModalId = cm.Id
                   LEFT JOIN employeescheduleshiftclaim esc ON css.Id = esc.ClientScheduleShiftID
            WHERE  cm.Id IS NULL
              AND  css.ModalId > 0
              AND  css.todate > NOW()
              AND  esc.ClientScheduleShiftID IS NULL";

        return await BatchDeleteByIdsAsync(findOrphansSql, "DeleteOrphanedShifts", ct);
    }

    /// <summary>
    /// Delete shifts for models that have been reset (IsModelReset=1) or deactivated (IsActive=0).
    /// Uses fromdate > CURDATE() (index-friendly, no date() wrapping).
    /// </summary>
    private async Task<int> DeleteResetInactiveShiftsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting reset/inactive shift deletion...");

        const string findResetSql = @"
            SELECT css.Id
            FROM   clientscheduleshift css
                   INNER JOIN clientschedulemodel cm
                          ON css.ModalId = cm.Id
                         AND (cm.IsModelReset = 1 OR cm.IsActive = 0)
                   LEFT JOIN employeescheduleshiftclaim esc ON css.Id = esc.ClientScheduleShiftID
            WHERE  css.fromdate > CURDATE()
              AND  esc.ClientScheduleShiftID IS NULL";

        return await BatchDeleteByIdsAsync(findResetSql, "DeleteResetInactiveShifts", ct);
    }

    /// <summary>
    /// Generic batch-delete pattern: pre-compute all IDs to delete in one query,
    /// then delete in batches with retry logic and sleep between batches.
    /// </summary>
    private async Task<int> BatchDeleteByIdsAsync(string findIdsSql, string operationName, CancellationToken ct)
    {
        List<int> idsToDelete;

        // Pre-compute IDs (short-lived read lock, released immediately)
        await using (var conn = await _dbFactory.CreateConnectionAsync(ct))
        {
            var ids = await conn.QueryAsync<int>(findIdsSql, commandTimeout: 300);
            idsToDelete = ids.ToList();
        }

        _logger.LogInformation("{Operation}: found {Count} rows to delete", operationName, idsToDelete.Count);

        if (idsToDelete.Count == 0)
            return 0;

        // Batch delete using primary key lookups (fast, minimal locking)
        int totalDeleted = 0;

        foreach (var batch in idsToDelete.Chunk(_config.DeleteBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                int deleted = await conn.ExecuteAsync(
                    "DELETE FROM clientscheduleshift WHERE Id IN @Ids",
                    new { Ids = batch },
                    commandTimeout: 120);
                totalDeleted += deleted;
            }, operationName, ct);

            // Yield to web/mobile/alert traffic
            if (_config.SleepBetweenBatchesMs > 0)
                await Task.Delay(_config.SleepBetweenBatchesMs, ct);
        }

        _logger.LogInformation("{Operation}: deleted {Count} total rows", operationName, totalDeleted);
        return totalDeleted;
    }

    /// <summary>
    /// Port of ClientShiftModalEditable stored procedure.
    /// For multi-week models that have been reset (IsModelReset=1):
    ///   - Finds the last historical schedule date from job_clientschedulefunctiondataHistory
    ///   - Updates job_ClientscheduleShiftnextrunStatus with that date and sets ModalEditmode=1
    ///   - This causes SpanClientScheduleShift logic to regenerate from today instead of
    ///     continuing from where it left off
    /// </summary>
    private async Task ResetEditedModelAnchorsAsync(CancellationToken ct)
    {
        // Find all multi-week models that have been reset
        const string findResetModelsSql = @"
            SELECT Id FROM clientschedulemodel
            WHERE isActive = 1 AND recurringon > 1 AND IsModelReset = 1";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var resetModelIds = (await conn.QueryAsync<int>(findResetModelsSql, commandTimeout: 60)).ToList();

        if (resetModelIds.Count == 0)
            return;

        _logger.LogInformation("Resetting anchor dates for {Count} edited multi-week models", resetModelIds.Count);

        foreach (int modalId in resetModelIds)
        {
            // Find last historical schedule date for this model
            var lastDate = await conn.QuerySingleOrDefaultAsync<DateTime?>(
                @"SELECT scheduledate FROM job_clientschedulefunctiondataHistory
                  WHERE DATE(scheduledate) < NOW() AND modal_id = @ModalId
                  ORDER BY scheduledate DESC LIMIT 1",
                new { ModalId = modalId },
                commandTimeout: 30);

            if (lastDate.HasValue)
            {
                await conn.ExecuteAsync(
                    @"UPDATE job_ClientscheduleShiftnextrunStatus
                      SET Nextscheduledate = @NextDate, ModalEditmode = 1
                      WHERE modal_id = @ModalId",
                    new { NextDate = lastDate.Value, ModalId = modalId },
                    commandTimeout: 30);
            }
            else
            {
                // No history — use the model's UpdateDate as fallback
                var updateDate = await conn.QuerySingleOrDefaultAsync<DateTime>(
                    "SELECT UpdateDate FROM clientschedulemodel WHERE Id = @ModalId",
                    new { ModalId = modalId },
                    commandTimeout: 30);

                await conn.ExecuteAsync(
                    @"UPDATE job_ClientscheduleShiftnextrunStatus
                      SET Nextscheduledate = @NextDate, ModalEditmode = 1
                      WHERE modal_id = @ModalId",
                    new { NextDate = updateDate, ModalId = modalId },
                    commandTimeout: 30);
            }
        }
    }

    /// <summary>
    /// Clear IsModelReset flags and set lastrundate to yesterday for reset models.
    /// Mirrors schedular.sql line 158.
    /// </summary>
    private async Task ClearModelResetFlagsAsync(CancellationToken ct)
    {
        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        int affected = await conn.ExecuteAsync(
            @"UPDATE clientschedulemodel
              SET lastrundate = DATE_ADD(NOW(), INTERVAL -1 DAY), IsModelReset = 0
              WHERE IsModelReset = 1",
            commandTimeout: 120);

        if (affected > 0)
            _logger.LogInformation("Cleared IsModelReset flag on {Count} models", affected);
    }

    /// <summary>
    /// Clean working/temp tables used by the scheduling process.
    /// Mirrors schedular.sql lines 160-163.
    /// </summary>
    private async Task CleanWorkingTablesAsync(CancellationToken ct)
    {
        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            DELETE FROM job_clientschedulemodel;
            DELETE FROM job_clientscheduletempfunctiondata;
            DELETE FROM job_clientscheduletempfunctiondataweekly;
            DELETE FROM job_clientschedulefunctiondataHistory
                   WHERE DATE(scheduledate) < DATE(DATE_ADD(NOW(), INTERVAL -@RetentionDays DAY));",
            new { RetentionDays = _config.HistoryRetentionDays },
            commandTimeout: 120);

        _logger.LogInformation("Working tables cleaned (history retention: {Days} days)", _config.HistoryRetentionDays);
    }
}
