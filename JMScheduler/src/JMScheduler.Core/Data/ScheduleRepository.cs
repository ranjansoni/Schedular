using System.Text;
using Dapper;
using MySqlConnector;
using Microsoft.Extensions.Logging;
using JMScheduler.Core.Configuration;
using JMScheduler.Core.Infrastructure;
using JMScheduler.Core.Models;

namespace JMScheduler.Core.Data;

/// <summary>
/// All database access for the scheduler job. Encapsulates the queries
/// that were previously scattered across multiple stored procedures.
///
/// Design principles:
///   - One connection per operation (no shared state)
///   - Deadlock retry via DeadlockRetryHandler
///   - All SQL uses parameterized queries (no string interpolation of values)
///   - Timeout set on every query (no unbounded waits)
/// </summary>
public sealed class ScheduleRepository
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly DeadlockRetryHandler _retryHandler;
    private readonly SchedulerConfig _config;
    private readonly ILogger<ScheduleRepository> _logger;

    public ScheduleRepository(
        DbConnectionFactory dbFactory,
        DeadlockRetryHandler retryHandler,
        SchedulerConfig config,
        ILogger<ScheduleRepository> logger)
    {
        _dbFactory    = dbFactory;
        _retryHandler = retryHandler;
        _config       = config;
        _logger       = logger;
    }

    // ========================================================================
    // MODEL LOADING
    // Replaces: INSERT INTO job_clientschedulemodel ... (processScheduleModel.sql lines 106-133)
    // ========================================================================

    /// <summary>
    /// Load all active, eligible weekly/multi-week models (RecurringType = 0) in a single query.
    /// Joins clientdetail + companydetail to ensure both the client and company are active.
    /// </summary>
    public async Task<List<ScheduleModel>> LoadWeeklyModelsAsync(
        DateTime scheduleDateTime, CancellationToken ct,
        int companyId = 0, int modelId = 0)
    {
        var sql = @"
            SELECT
                csm.Id, csm.employeeid AS EmployeeId, csm.Client_id,
                csm.startdate AS StartDate, csm.enddate AS EndDate,
                csm.fromdate AS FromDate, csm.todate AS ToDate,
                csm.lastrundate AS LastRunDate,
                csm.timein AS TimeIn, csm.timeout AS TimeOut,
                csm.duration AS Duration,
                csm.RecurringType, csm.recurringon AS RecurringOn,
                csm.MonthlyRecurringType, csm.IsModelReset, csm.IsActive,
                csm.noenddate AS NoEndDate,
                csm.sunday AS Sunday, csm.monday AS Monday, csm.tuesday AS Tuesday,
                csm.wednesday AS Wednesday, csm.thursday AS Thursday,
                csm.friday AS Friday, csm.saturday AS Saturday,
                csm.IsLateInAlert, csm.LateInDuration,
                csm.IsLateOutAlert, csm.LateOutDuration,
                csm.IsCustomInAlert, csm.IsCustomOutAlert,
                csm.IsAutoClockOut, csm.AutoClockOutSelectedValue,
                csm.AutoClockOutHour, csm.AutoClockOutMinutes,
                csm.JobClassification_Id, csm.IsTeamSchedule,
                csm.GroupScheduleId, csm.IsRounding, csm.RoundUp, csm.RoundDown,
                csm.IsFlatRate, csm.FlatRate,
                csm.IsOpenSchedule, csm.IsPublished,
                csm.IsScheduleClockInRestrictionEnable,
                csm.IsScheduleClockOutRestrictionEnable,
                csm.IsScheduleDurationRestrictionEnable,
                csm.ScheduleRestrictClockInBefore, csm.ScheduleRestrictClockInAfter,
                csm.ScheduleRestrictClockOutBefore, csm.ScheduleRestrictClockOutAfter,
                csm.ScheduleRestrictMinDuration, csm.ScheduleRestrictMaxDuration,
                csm.IsScheduleRestrictionEnable, csm.CompanyID, csm.ScheduleType,
                csm.BreakDetailID, csm.IsSuppressedScheduleRestriction,
                csm.IsManagerApprovalEnabled, csm.ScheduleScanType, csm.UserNote
            FROM  clientschedulemodel csm
                  INNER JOIN clientdetail cd
                         ON csm.Client_id = cd.Id AND cd.IsActive = 1
                  INNER JOIN companydetail co
                         ON co.Id = cd.company_id
                        AND co.IsActive = 1
                        AND co.AccountStatus = 'Active'
            WHERE csm.isActive = 1
              AND csm.RecurringType = 0
              AND (csm.enddate = '0001-01-01' OR DATE(csm.enddate) >= DATE(@ScheduleDate))
              AND (csm.lastrundate = '0001-01-01' OR DATE(csm.lastrundate) < CURDATE())";

        if (companyId > 0)
            sql += " AND csm.CompanyID = @CompanyId";
        if (modelId > 0)
            sql += " AND csm.Id = @ModelId";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var models = await conn.QueryAsync<ScheduleModel>(
            sql, new { ScheduleDate = scheduleDateTime, CompanyId = companyId, ModelId = modelId },
            commandTimeout: 300);

        var result = models.ToList();
        _logger.LogInformation("Loaded {Count} weekly/multi-week models (RecurringType=0)", result.Count);
        return result;
    }

    /// <summary>
    /// Load all active, eligible monthly models (RecurringType = 1).
    /// Monthly models use a different eligibility check based on month/year of lastrundate.
    /// Mirrors: MonthlySchedular.sql lines 65-70 WHERE clause.
    /// </summary>
    public async Task<List<ScheduleModel>> LoadMonthlyModelsAsync(
        DateTime scheduleDate, CancellationToken ct,
        int companyId = 0, int modelId = 0)
    {
        var sql = @"
            SELECT
                csm.Id, csm.employeeid AS EmployeeId, csm.Client_id,
                csm.startdate AS StartDate, csm.enddate AS EndDate,
                csm.fromdate AS FromDate, csm.todate AS ToDate,
                csm.lastrundate AS LastRunDate,
                csm.timein AS TimeIn, csm.timeout AS TimeOut,
                csm.duration AS Duration,
                csm.RecurringType, csm.recurringon AS RecurringOn,
                csm.MonthlyRecurringType, csm.IsModelReset, csm.IsActive,
                csm.noenddate AS NoEndDate,
                csm.sunday AS Sunday, csm.monday AS Monday, csm.tuesday AS Tuesday,
                csm.wednesday AS Wednesday, csm.thursday AS Thursday,
                csm.friday AS Friday, csm.saturday AS Saturday,
                csm.IsLateInAlert, csm.LateInDuration,
                csm.IsLateOutAlert, csm.LateOutDuration,
                csm.IsCustomInAlert, csm.IsCustomOutAlert,
                csm.IsAutoClockOut, csm.AutoClockOutSelectedValue,
                csm.AutoClockOutHour, csm.AutoClockOutMinutes,
                csm.JobClassification_Id, csm.IsTeamSchedule,
                csm.GroupScheduleId, csm.IsRounding, csm.RoundUp, csm.RoundDown,
                csm.IsFlatRate, csm.FlatRate,
                csm.IsOpenSchedule, csm.IsPublished,
                csm.IsScheduleClockInRestrictionEnable,
                csm.IsScheduleClockOutRestrictionEnable,
                csm.IsScheduleDurationRestrictionEnable,
                csm.ScheduleRestrictClockInBefore, csm.ScheduleRestrictClockInAfter,
                csm.ScheduleRestrictClockOutBefore, csm.ScheduleRestrictClockOutAfter,
                csm.ScheduleRestrictMinDuration, csm.ScheduleRestrictMaxDuration,
                csm.IsScheduleRestrictionEnable, csm.CompanyID, csm.ScheduleType,
                csm.BreakDetailID, csm.IsSuppressedScheduleRestriction,
                csm.IsManagerApprovalEnabled, csm.ScheduleScanType, csm.UserNote
            FROM  clientschedulemodel csm
                  INNER JOIN clientdetail cd
                         ON csm.Client_id = cd.Id AND cd.IsActive = 1
                  INNER JOIN companydetail co
                         ON co.Id = cd.company_id
                        AND co.IsActive = 1
                        AND co.AccountStatus = 'Active'
            WHERE csm.isActive = 1
              AND csm.RecurringType = 1
              AND DATE(@ScheduleDate) >= DATE(csm.startdate)
              AND (DATE(csm.enddate) = DATE('0001-01-01') OR DATE(@ScheduleDate) <= DATE(csm.enddate))
              AND (csm.lastrundate IS NULL
                   OR DATE(csm.lastrundate) <= LAST_DAY(@ScheduleDate))";

        if (companyId > 0)
            sql += " AND csm.CompanyID = @CompanyId";
        if (modelId > 0)
            sql += " AND csm.Id = @ModelId";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var models = await conn.QueryAsync<ScheduleModel>(
            sql, new { ScheduleDate = scheduleDate, CompanyId = companyId, ModelId = modelId },
            commandTimeout: 300);

        var result = models.ToList();
        _logger.LogInformation("Loaded {Count} monthly models (RecurringType=1) for {Date:yyyy-MM}",
            result.Count, scheduleDate);
        return result;
    }

    // ========================================================================
    // DUPLICATE CHECK
    // Replaces: per-row COUNT(*) against 4.5M-row clientscheduleshift table
    // ========================================================================

    /// <summary>
    /// Bulk-load all existing shift keys for a date range into a HashSet.
    /// Key format: "ClientId|EmployeeId|DateTimeIn|DateTimeOut"
    /// One query replaces hundreds of thousands of COUNT(*) queries.
    /// 
    /// Note: We scope by datetimein only (not datetimeout) because overnight and multi-day
    /// shifts can have datetimeout far beyond the advance window. Scoping by datetimeout
    /// would miss those shifts and fail to deduplicate them.
    /// </summary>
    public async Task<HashSet<string>> LoadExistingShiftKeysAsync(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        const string sql = @"
            SELECT CONCAT(Client_id, '|', employeeid, '|',
                          DATE_FORMAT(datetimein, '%Y-%m-%d %H:%i'), '|',
                          DATE_FORMAT(datetimeout, '%Y-%m-%d %H:%i'))
            FROM   clientscheduleshift
            WHERE  datetimein >= @StartDate
              AND  datetimein <= @EndDate";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var keys = await conn.QueryAsync<string>(
            sql,
            new { StartDate = startDate.Date, EndDate = endDate.Date.AddDays(1) },
            commandTimeout: 300);

        var hashSet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Loaded {Count} existing shift keys for duplicate check", hashSet.Count);
        return hashSet;
    }

    /// <summary>
    /// Build the duplicate-check key format matching LoadExistingShiftKeysAsync.
    /// </summary>
    public static string BuildShiftKey(int clientId, int employeeId, DateTime dateTimeIn, DateTime dateTimeOut)
    {
        return $"{clientId}|{employeeId}|{dateTimeIn:yyyy-MM-dd HH:mm}|{dateTimeOut:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Load model-aware shift keys for ScheduleType=1 (OpenWithAllClaim) deduplication.
    /// Key format: "ModalId|ClientId|EmployeeId|DateTimeIn|DateTimeOut".
    ///
    /// ScheduleType=1 allows multiple models to create shifts for the same client/employee/time,
    /// but the SAME model must NOT create duplicate shifts across runs. Including ModalId in the
    /// key ensures cross-model shifts are preserved while cross-run duplicates are caught.
    /// </summary>
    public async Task<HashSet<string>> LoadExistingModalShiftKeysAsync(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        const string sql = @"
            SELECT CONCAT(ModalId, '|', Client_id, '|', employeeid, '|',
                          DATE_FORMAT(datetimein, '%Y-%m-%d %H:%i'), '|',
                          DATE_FORMAT(datetimeout, '%Y-%m-%d %H:%i'))
            FROM   clientscheduleshift
            WHERE  datetimein >= @StartDate
              AND  datetimein <= @EndDate";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var keys = await conn.QueryAsync<string>(
            sql,
            new { StartDate = startDate.Date, EndDate = endDate.Date.AddDays(1) },
            commandTimeout: 300);

        var hashSet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Loaded {Count} existing modal shift keys for ScheduleType=1 dedup", hashSet.Count);
        return hashSet;
    }

    // ========================================================================
    // MODEL CATEGORIZATION (fast path vs slow path)
    // Pre-query which models need post-insert processing
    // ========================================================================

    /// <summary>
    /// Get the set of model IDs that have scan area templates.
    /// These need ProcessRecurring_ScheduleScanArea called after insert (slow path).
    /// </summary>
    public async Task<HashSet<int>> LoadModelsWithScanAreasAsync(CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT ModalId FROM scheduleshiftscandetailmodel";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var ids = await conn.QueryAsync<int>(sql, commandTimeout: 60);
        var set = ids.ToHashSet();

        _logger.LogInformation("Found {Count} models with scan area templates", set.Count);
        return set;
    }

    /// <summary>
    /// Get the set of model IDs that have employee claim templates.
    /// These need employeescheduleshiftmodelclaim → employeescheduleshiftclaim copy after insert (slow path, weekly only).
    /// </summary>
    public async Task<HashSet<int>> LoadModelsWithClaimsAsync(CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT ClientScheduleShiftModelID FROM employeescheduleshiftmodelclaim";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var ids = await conn.QueryAsync<int>(sql, commandTimeout: 60);
        var set = ids.ToHashSet();

        _logger.LogInformation("Found {Count} models with claim templates", set.Count);
        return set;
    }

    // ========================================================================
    // MULTI-WEEK TRACKING
    // Replaces: job_ClientscheduleShiftnextrunStatus queries in SpanClientScheduleShift
    // ========================================================================

    /// <summary>
    /// Load all tracking rows for multi-week models.
    /// </summary>
    public async Task<Dictionary<int, NextRunStatus>> LoadMultiWeekTrackingAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT modal_id AS ModalId, employeeid AS EmployeeId, Client_id,
                   Nextscheduledate, Changestatus, ModalEditmode
            FROM   job_ClientscheduleShiftnextrunStatus";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var rows = await conn.QueryAsync<NextRunStatus>(sql, commandTimeout: 60);
        var dict = rows.ToDictionary(r => r.ModalId);

        _logger.LogInformation("Loaded {Count} multi-week tracking rows", dict.Count);
        return dict;
    }

    /// <summary>
    /// Get the last shift date (datetimein) for a model — used as restriction date for multi-week.
    /// </summary>
    public async Task<DateTime?> GetLastShiftDateForModelAsync(int modalId, CancellationToken ct)
    {
        const string sql = @"
            SELECT MAX(DATE(datetimein))
            FROM   clientscheduleshift
            WHERE  ModalId = @ModalId AND IsActive = 1";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DateTime?>(
            sql, new { ModalId = modalId }, commandTimeout: 30);
    }

    /// <summary>
    /// BULK version: Get the last shift date for ALL given model IDs in a single query.
    /// Replaces 281 individual GetLastShiftDateForModelAsync calls with 1 query.
    /// Returns a dictionary of ModalId → last shift date.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> GetLastShiftDatesForModelsAsync(
        IEnumerable<int> modalIds, CancellationToken ct)
    {
        var idList = modalIds.ToList();
        if (idList.Count == 0) return new Dictionary<int, DateTime>();

        const string sql = @"
            SELECT ModalId, MAX(DATE(datetimein)) AS LastDate
            FROM   clientscheduleshift
            WHERE  ModalId IN @Ids AND IsActive = 1
            GROUP BY ModalId";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var rows = await conn.QueryAsync<(int ModalId, DateTime LastDate)>(
            sql, new { Ids = idList }, commandTimeout: 120);

        return rows.ToDictionary(r => r.ModalId, r => r.LastDate);
    }

    /// <summary>
    /// Get the last history date for a model where a matching shift exists.
    /// Used for resolving anchor date in multi-week calculation.
    /// </summary>
    public async Task<DateTime?> GetLastHistoryDateForModelAsync(int modalId, CancellationToken ct)
    {
        const string sql = @"
            SELECT h.scheduledate
            FROM   job_clientschedulefunctiondataHistory h
            WHERE  h.modal_id = @ModalId
              AND  DATE(h.scheduledate) < NOW()
              AND  EXISTS (
                     SELECT 1 FROM clientscheduleshift css
                     WHERE css.ModalId = h.modal_id
                       AND DATE(css.datetimein) = DATE(h.scheduledate)
                   )
            ORDER BY h.scheduledate DESC
            LIMIT 1";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DateTime?>(
            sql, new { ModalId = modalId }, commandTimeout: 30);
    }

    /// <summary>
    /// BULK version: Get the last confirmed history date for ALL given model IDs in a single query.
    /// Uses a correlated subquery to find the MAX history date where a matching shift exists.
    /// Replaces 281 individual GetLastHistoryDateForModelAsync calls with 1 query.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> GetLastHistoryDatesForModelsAsync(
        IEnumerable<int> modalIds, CancellationToken ct)
    {
        var idList = modalIds.ToList();
        if (idList.Count == 0) return new Dictionary<int, DateTime>();

        // For each model, find the latest history date that has a matching shift
        const string sql = @"
            SELECT h.modal_id AS ModalId, MAX(h.scheduledate) AS LastDate
            FROM   job_clientschedulefunctiondataHistory h
                   INNER JOIN clientscheduleshift css
                          ON css.ModalId = h.modal_id
                         AND DATE(css.datetimein) = DATE(h.scheduledate)
            WHERE  h.modal_id IN @Ids
              AND  DATE(h.scheduledate) < NOW()
            GROUP BY h.modal_id";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var rows = await conn.QueryAsync<(int ModalId, DateTime LastDate)>(
            sql, new { Ids = idList }, commandTimeout: 120);

        return rows.ToDictionary(r => r.ModalId, r => r.LastDate);
    }

    /// <summary>
    /// Update tracking status after processing: set Changestatus=1 for models that had shifts created.
    /// </summary>
    public async Task MarkMultiWeekTrackingChangedAsync(IEnumerable<int> modalIds, CancellationToken ct)
    {
        var idList = modalIds.ToList();
        if (idList.Count == 0) return;

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE job_ClientscheduleShiftnextrunStatus SET Changestatus = 1 WHERE modal_id IN @Ids",
            new { Ids = idList },
            commandTimeout: 60);
    }

    /// <summary>
    /// Finalize multi-week tracking: update Nextscheduledate, clear Changestatus and ModalEditmode.
    /// Called at the end of the last day's processing (equivalent of advanceDays=0 block in SP).
    /// </summary>
    public async Task FinalizeMultiWeekTrackingAsync(
        int modalId, DateTime nextScheduleDate, CancellationToken ct)
    {
        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        await conn.ExecuteAsync(
            @"UPDATE job_ClientscheduleShiftnextrunStatus
              SET Nextscheduledate = @NextDate, Changestatus = 0, ModalEditmode = 0
              WHERE modal_id = @ModalId",
            new { NextDate = nextScheduleDate, ModalId = modalId },
            commandTimeout: 30);
    }

    // ========================================================================
    // FAST PATH: BULK INSERT (no post-insert processing needed)
    // Replaces: per-row INSERT in the 7x day-of-week blocks
    // ========================================================================

    /// <summary>
    /// Bulk-insert shifts using multi-row INSERT VALUES syntax.
    /// Used for shifts that do NOT need scan areas, claims, or group schedule handling.
    /// </summary>
    public async Task<int> BulkInsertShiftsAsync(
        IReadOnlyList<ScheduleShift> shifts, CancellationToken ct)
    {
        if (shifts.Count == 0) return 0;

        int totalInserted = 0;

        foreach (var batch in shifts.Chunk(_config.InsertBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                var inserted = await InsertBatchAsync(conn, batch, ct);
                totalInserted += inserted;
            }, "BulkInsertShifts", ct);

            if (_config.SleepBetweenBatchesMs > 0)
                await Task.Delay(_config.SleepBetweenBatchesMs, ct);
        }

        return totalInserted;
    }

    // ========================================================================
    // SLOW PATH: INDIVIDUAL INSERT + POST-INSERT PROCESSING
    // For shifts needing scan areas, claims, or group schedule handling
    // ========================================================================

    /// <summary>
    /// Insert a single shift and return the auto-generated ID via LAST_INSERT_ID().
    /// Required because innodb_autoinc_lock_mode=2 means bulk inserts don't return contiguous IDs.
    /// </summary>
    public async Task<long> InsertShiftAndGetIdAsync(
        MySqlConnection conn, ScheduleShift shift, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO clientscheduleshift (
                ModalId, employeeid, Client_id, fromdate, todate, datetimein, datetimeout,
                duration, actualtimein, actualtimeout, actualduration, note,
                CreateDate, UpdateDate, IsActive, DeactivationDate, CreateUser_id, UpdateUser_id,
                timein, timeout, Employeetimecard_id,
                IsInMissing, IsOutMissing, IsLateIn, IsEarlyOut,
                IsInMissingAlertSent, IsOutMissingAlertSent, IsLateInAlertSent, IsEarlyOutAlertSent,
                IsLateInAlert, LateInDuration, IsLateOutAlert, LateOutDuration,
                IsCustomInAlert, IsCustomOutAlert, WorkOrderID,
                IsAutoClockOut, AutoClockOutSelectedValue, AutoClockOutHour, AutoClockOutMinutes,
                JobClassification_Id, IsTeamSchedule, GroupScheduleId,
                IsRounding, RoundUp, RoundDown, IsFlatRate, FlatRate,
                IsOpenSchedule, IsPublished,
                IsScheduleClockInRestrictionEnable, IsScheduleClockOutRestrictionEnable,
                IsScheduleDurationRestrictionEnable,
                ScheduleRestrictClockInBefore, ScheduleRestrictClockInAfter,
                ScheduleRestrictClockOutBefore, ScheduleRestrictClockOutAfter,
                ScheduleRestrictMinDuration, ScheduleRestrictMaxDuration,
                IsScheduleRestrictionEnable, CompanyID, ScheduleType, BreakDetailID,
                IsSuppressedScheduleRestriction, IsManagerApprovalEnabled,
                ScheduleScanType, UserNote
            ) VALUES (
                @ModalId, @EmployeeId, @ClientId, @FromDate, @ToDate,
                @DateTimeIn, @DateTimeOut, @Duration, NULL, NULL, NULL, @Note,
                @CreateDate, @UpdateDate, @IsActive, NULL, @CreateUserId, @UpdateUserId,
                @TimeIn, @TimeOut, NULL,
                0, 0, 0, 0, 0, 0, 0, 0,
                @IsLateInAlert, @LateInDuration, @IsLateOutAlert, @LateOutDuration,
                @IsCustomInAlert, @IsCustomOutAlert, NULL,
                @IsAutoClockOut, @AutoClockOutSelectedValue, @AutoClockOutHour, @AutoClockOutMinutes,
                @JobClassificationId, @IsTeamSchedule, @GroupScheduleId,
                @IsRounding, @RoundUp, @RoundDown, @IsFlatRate, @FlatRate,
                @IsOpenSchedule, @IsPublished,
                @IsClockInRestriction, @IsClockOutRestriction, @IsDurationRestriction,
                @RestrictClockInBefore, @RestrictClockInAfter,
                @RestrictClockOutBefore, @RestrictClockOutAfter,
                @RestrictMinDuration, @RestrictMaxDuration,
                @IsRestrictionEnable, @CompanyID, @ScheduleType, NULLIF(@BreakDetailID, 0),
                @IsSuppressed, @IsManagerApproval, @ScanType, @UserNote
            );
            SELECT LAST_INSERT_ID();";

        return await conn.QuerySingleAsync<long>(
            sql, BuildShiftParams(shift), commandTimeout: 30);
    }

    /// <summary>
    /// Copy claim templates from employeescheduleshiftmodelclaim to employeescheduleshiftclaim.
    /// Weekly behavior only — monthly does NOT copy claims.
    /// Mirrors: processScheduleModel.sql line 245.
    /// </summary>
    public async Task CopyModelClaimsToShiftAsync(
        MySqlConnection conn, int modelId, long shiftId, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO employeescheduleshiftclaim
                (DateCreated, DateModified, EmployeeID, ClientScheduleShiftID, CompanyID, JobclassID)
            SELECT DateCreated, DateModified, EmployeeID, @ShiftId, CompanyID, JobclassID
            FROM   employeescheduleshiftmodelclaim
            WHERE  ClientScheduleShiftModelID = @ModelId";

        await conn.ExecuteAsync(sql, new { ShiftId = shiftId, ModelId = modelId }, commandTimeout: 30);
    }

    /// <summary>
    /// Call the existing ProcessRecurring_ScheduleScanArea stored procedure.
    /// This copies scan area + scan task templates from the model to the new shift.
    /// Called for non-group shifts only (both weekly and monthly).
    /// DEPRECATED: Use BulkCopyScanAreasAsync for better performance.
    /// </summary>
    public async Task CallProcessRecurringScanAreaAsync(
        MySqlConnection conn, int modelId, long shiftId, CancellationToken ct)
    {
        await conn.ExecuteAsync(
            "CALL ProcessRecurring_ScheduleScanArea(@ModelId, @ShiftId)",
            new { ModelId = modelId, ShiftId = shiftId },
            commandTimeout: 60);
    }

    /// <summary>
    /// BULK copy scan areas + scan tasks for all newly inserted shifts matching the given model IDs
    /// on a target date. Replaces hundreds of individual ProcessRecurring_ScheduleScanArea SP calls
    /// with 2 bulk INSERT...SELECT queries.
    ///
    /// Phase 1: Copy scheduleshiftscandetailmodel → scheduleshiftscandetail for all new shifts
    /// Phase 2: Copy scheduleshiftscantaskdetailmodel → scheduleshiftscantaskdetail using join
    ///
    /// Returns the number of scan detail rows created.
    /// </summary>
    public async Task<int> BulkCopyScanAreasAsync(
        IReadOnlyList<int> modelIds, DateTime targetDate, CancellationToken ct)
    {
        if (modelIds.Count == 0) return 0;

        int totalScanDetails = 0;

        // Process in batches to avoid MySQL parameter limits
        foreach (var batch in modelIds.Chunk(_config.InsertBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);

                // Phase 1: Bulk copy scan detail models → scan details
                // Joins newly inserted shifts (by ModalId + date) to their scan templates
                const string copyScanDetailsSql = @"
                    INSERT INTO scheduleshiftscandetail
                        (ShiftId, ClientId, CompanyId, ScanType, ClientAreaId, ClientAreaWorkTemplateId,
                         EmployeeId, DateCreated, DateModified, TimecardId, IsActive, Status,
                         StartTime, EndTime, Minutes, Sqft, Title, Description)
                    SELECT css.Id, sdm.ClientId, sdm.CompanyId, sdm.ScanType,
                           sdm.ClientAreaId, sdm.ClientAreaWorkTemplateId,
                           sdm.EmployeeId, NOW(), NOW(), sdm.TimecardId, sdm.IsActive, sdm.Status,
                           sdm.StartTime, sdm.EndTime, sdm.Minutes, sdm.Sqft, sdm.Title, sdm.Description
                    FROM   clientscheduleshift css
                           INNER JOIN scheduleshiftscandetailmodel sdm
                                  ON sdm.ModalId = css.ModalId
                                 AND sdm.EmployeeId = css.employeeid
                    WHERE  css.ModalId IN @ModelIds
                      AND  DATE(css.datetimein) = @TargetDate
                      AND  NOT EXISTS (
                               SELECT 1 FROM scheduleshiftscandetail x WHERE x.ShiftId = css.Id
                           )";

                var scanDetailsInserted = await conn.ExecuteAsync(
                    copyScanDetailsSql,
                    new { ModelIds = batch, TargetDate = targetDate.Date },
                    commandTimeout: 120);

                totalScanDetails += scanDetailsInserted;

                // Phase 2: Bulk copy scan task detail models → scan task details
                // Joins the newly created scan details back to their source model rows
                // to find the matching task templates
                const string copyScanTasksSql = @"
                    INSERT INTO scheduleshiftscantaskdetail
                        (ScheduleShiftScanId, DateCreated, DateModified, Title, Description,
                         IsActive, IsMandatory, OrderNumber, Status, CompletionTime, Latitude, Longitude)
                    SELECT ssd.Id, stdm.DateCreated, stdm.DateModified, stdm.Title, stdm.Description,
                           stdm.IsActive, stdm.IsMandatory, stdm.OrderNumber, stdm.Status,
                           stdm.CompletionTime, stdm.Latitude, stdm.Longitude
                    FROM   scheduleshiftscandetail ssd
                           INNER JOIN clientscheduleshift css
                                  ON css.Id = ssd.ShiftId
                           INNER JOIN scheduleshiftscandetailmodel sdm
                                  ON sdm.ModalId = css.ModalId
                                 AND sdm.EmployeeId = css.employeeid
                                 AND sdm.ClientAreaId = ssd.ClientAreaId
                                 AND sdm.ClientAreaWorkTemplateId = ssd.ClientAreaWorkTemplateId
                           INNER JOIN scheduleshiftscantaskdetailmodel stdm
                                  ON stdm.scheduleshiftscandetailmodelId = sdm.Id
                    WHERE  css.ModalId IN @ModelIds
                      AND  DATE(css.datetimein) = @TargetDate
                      AND  NOT EXISTS (
                               SELECT 1 FROM scheduleshiftscantaskdetail x
                               WHERE x.ScheduleShiftScanId = ssd.Id
                           )";

                await conn.ExecuteAsync(
                    copyScanTasksSql,
                    new { ModelIds = batch, TargetDate = targetDate.Date },
                    commandTimeout: 120);

            }, "BulkCopyScanAreas", ct);
        }

        return totalScanDetails;
    }

    /// <summary>
    /// BULK copy claims for all newly inserted shifts matching the given model IDs on a target date.
    /// Replaces hundreds of individual CopyModelClaimsToShiftAsync calls with 1 bulk INSERT...SELECT.
    ///
    /// Copies employeescheduleshiftmodelclaim → employeescheduleshiftclaim for all new shifts.
    /// Returns the number of claim rows created.
    /// </summary>
    public async Task<int> BulkCopyClaimsAsync(
        IReadOnlyList<int> modelIds, DateTime targetDate, CancellationToken ct)
    {
        if (modelIds.Count == 0) return 0;

        int totalClaims = 0;

        foreach (var batch in modelIds.Chunk(_config.InsertBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);

                const string sql = @"
                    INSERT INTO employeescheduleshiftclaim
                        (DateCreated, DateModified, EmployeeID, ClientScheduleShiftID, CompanyID, JobclassID)
                    SELECT mc.DateCreated, mc.DateModified, mc.EmployeeID, css.Id, mc.CompanyID, mc.JobclassID
                    FROM   clientscheduleshift css
                           INNER JOIN employeescheduleshiftmodelclaim mc
                                  ON mc.ClientScheduleShiftModelID = css.ModalId
                    WHERE  css.ModalId IN @ModelIds
                      AND  DATE(css.datetimein) = @TargetDate
                      AND  NOT EXISTS (
                               SELECT 1 FROM employeescheduleshiftclaim ec
                               WHERE ec.ClientScheduleShiftID = css.Id
                           )";

                var inserted = await conn.ExecuteAsync(
                    sql,
                    new { ModelIds = batch, TargetDate = targetDate.Date },
                    commandTimeout: 120);

                totalClaims += inserted;

            }, "BulkCopyClaims", ct);
        }

        return totalClaims;
    }

    // ========================================================================
    // GROUP SCHEDULE HANDLING
    // ========================================================================

    /// <summary>
    /// Clone an existing groupschedule row (weekly behavior).
    /// Mirrors: processScheduleModel.sql line 199-200.
    /// Returns the new group schedule ID.
    /// </summary>
    public async Task<int> CloneGroupScheduleAsync(
        MySqlConnection conn, int existingGroupId, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO groupschedule (Client_id, IsEmployeeSchedule, IsClientSchedule, DateCreated)
            SELECT Client_id, IsEmployeeSchedule, IsClientSchedule, NOW()
            FROM   groupschedule
            WHERE  Id = @GroupId;
            SELECT LAST_INSERT_ID();";

        return await conn.QuerySingleAsync<int>(sql, new { GroupId = existingGroupId }, commandTimeout: 30);
    }

    /// <summary>
    /// Insert a new groupschedule row (monthly behavior).
    /// Monthly creates a new row with fixed IsEmployeeSchedule=1, IsClientSchedule=0.
    /// Mirrors: MonthlySchedular.sql lines 191-193.
    /// Returns the new group schedule ID.
    /// </summary>
    public async Task<int> InsertNewGroupScheduleAsync(
        MySqlConnection conn, int clientId, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO groupschedule (Client_id, IsEmployeeSchedule, IsClientSchedule, DateCreated)
            VALUES (@ClientId, 1, 0, NOW());
            SELECT LAST_INSERT_ID();";

        return await conn.QuerySingleAsync<int>(sql, new { ClientId = clientId }, commandTimeout: 30);
    }

    /// <summary>
    /// For weekly group shifts: insert all models in the same group as individual shifts.
    /// The original SP does: SELECT ... FROM clientschedulemodel WHERE GroupScheduleId = P_GroupId
    /// and inserts a shift for each model in the group.
    ///
    /// Each member's shift is checked against the existingKeys/existingModalKeys HashSets
    /// before insertion. This prevents re-inserting shifts that already exist when a
    /// different model in the group (e.g. a ScheduleType=1 model) triggers the group path.
    /// </summary>
    public async Task<List<long>> InsertGroupShiftsAsync(
        MySqlConnection conn, int groupScheduleId, int newGroupId,
        DateTime scheduleDate, string note,
        HashSet<string> existingKeys, HashSet<string> existingModalKeys,
        CancellationToken ct)
    {
        // Load all models sharing this group
        var groupModels = await conn.QueryAsync<ScheduleModel>(
            @"SELECT Id, employeeid AS EmployeeId, Client_id, fromdate AS FromDate, todate AS ToDate,
                     timein AS TimeIn, timeout AS TimeOut, duration AS Duration,
                     IsLateInAlert, LateInDuration, IsLateOutAlert, LateOutDuration,
                     IsCustomInAlert, IsCustomOutAlert,
                     IsAutoClockOut, AutoClockOutSelectedValue, AutoClockOutHour, AutoClockOutMinutes,
                     JobClassification_Id, IsTeamSchedule, GroupScheduleId,
                     IsRounding, RoundUp, RoundDown, IsFlatRate, FlatRate,
                     IsOpenSchedule, IsPublished,
                     IsScheduleClockInRestrictionEnable, IsScheduleClockOutRestrictionEnable,
                     IsScheduleDurationRestrictionEnable,
                     ScheduleRestrictClockInBefore, ScheduleRestrictClockInAfter,
                     ScheduleRestrictClockOutBefore, ScheduleRestrictClockOutAfter,
                     ScheduleRestrictMinDuration, ScheduleRestrictMaxDuration,
                     IsScheduleRestrictionEnable, CompanyID, ScheduleType,
                     BreakDetailID, IsSuppressedScheduleRestriction,
                     IsManagerApprovalEnabled, ScheduleScanType, UserNote
              FROM   clientschedulemodel
              WHERE  GroupScheduleId = @GroupId",
            new { GroupId = groupScheduleId },
            commandTimeout: 60);

        var insertedIds = new List<long>();

        foreach (var gm in groupModels)
        {
            var shift = ScheduleShift.FromModelForGroup(gm, scheduleDate, newGroupId, note);

            // Dedup check: same logic as the main loop — prevent re-inserting members
            // that already have shifts for this date (e.g. from a previous run or earlier
            // in this run when a different model in the group triggered the group path).
            var key = shift.GetDuplicateKey();
            var modalKey = shift.GetModalDuplicateKey();
            bool alreadyExists = gm.ScheduleType == 1
                ? existingModalKeys.Contains(modalKey)
                : existingKeys.Contains(key);

            if (alreadyExists)
            {
                _logger.LogDebug(
                    "Group member skip: Modal={ModalId} already has shift for {Date:yyyy-MM-dd} (Group={GroupId})",
                    gm.Id, scheduleDate, groupScheduleId);
                continue;
            }

            long shiftId = await InsertShiftAndGetIdAsync(conn, shift, ct);
            insertedIds.Add(shiftId);

            // Register key so subsequent members/groups don't duplicate
            existingKeys.Add(key);
            existingModalKeys.Add(modalKey);
        }

        return insertedIds;
    }

    /// <summary>
    /// For monthly group shifts: insert all models in the same group with the new group ID.
    /// Monthly uses RecurringType=1 filter and non-null/non-zero GroupScheduleId.
    /// Mirrors: MonthlySchedular.sql lines 221-223.
    ///
    /// Each member's shift is checked against the existingKeys/existingModalKeys HashSets
    /// before insertion (same safeguard as the weekly group path).
    /// </summary>
    public async Task<List<long>> InsertMonthlyGroupShiftsAsync(
        MySqlConnection conn, int groupScheduleId, int newGroupId,
        DateTime scheduleDate,
        HashSet<string> existingKeys, HashSet<string> existingModalKeys,
        CancellationToken ct)
    {
        var groupModels = await conn.QueryAsync<ScheduleModel>(
            @"SELECT Id, employeeid AS EmployeeId, Client_id, fromdate AS FromDate, todate AS ToDate,
                     timein AS TimeIn, timeout AS TimeOut, duration AS Duration,
                     IsLateInAlert, LateInDuration, IsLateOutAlert, LateOutDuration,
                     IsCustomInAlert, IsCustomOutAlert,
                     IsAutoClockOut, AutoClockOutSelectedValue, AutoClockOutHour, AutoClockOutMinutes,
                     JobClassification_Id, IsTeamSchedule, GroupScheduleId,
                     IsRounding, RoundUp, RoundDown, IsFlatRate, FlatRate,
                     IsOpenSchedule, IsPublished,
                     IsScheduleClockInRestrictionEnable, IsScheduleClockOutRestrictionEnable,
                     IsScheduleDurationRestrictionEnable,
                     ScheduleRestrictClockInBefore, ScheduleRestrictClockInAfter,
                     ScheduleRestrictClockOutBefore, ScheduleRestrictClockOutAfter,
                     ScheduleRestrictMinDuration, ScheduleRestrictMaxDuration,
                     IsScheduleRestrictionEnable, CompanyID, ScheduleType,
                     BreakDetailID, IsSuppressedScheduleRestriction,
                     IsManagerApprovalEnabled, ScheduleScanType, UserNote
              FROM   clientschedulemodel
              WHERE  RecurringType = 1
                AND  GroupScheduleId = @GroupId
                AND  GroupScheduleId IS NOT NULL
                AND  GroupScheduleId != 0",
            new { GroupId = groupScheduleId },
            commandTimeout: 60);

        var insertedIds = new List<long>();

        foreach (var gm in groupModels)
        {
            var shift = ScheduleShift.FromModelForGroup(gm, scheduleDate, newGroupId, "Schedule Event Monthly");

            // Dedup check: prevent re-inserting members that already have shifts
            var key = shift.GetDuplicateKey();
            var modalKey = shift.GetModalDuplicateKey();
            bool alreadyExists = gm.ScheduleType == 1
                ? existingModalKeys.Contains(modalKey)
                : existingKeys.Contains(key);

            if (alreadyExists)
            {
                _logger.LogDebug(
                    "Monthly group member skip: Modal={ModalId} already has shift for {Date:yyyy-MM-dd} (Group={GroupId})",
                    gm.Id, scheduleDate, groupScheduleId);
                continue;
            }

            long shiftId = await InsertShiftAndGetIdAsync(conn, shift, ct);
            insertedIds.Add(shiftId);

            existingKeys.Add(key);
            existingModalKeys.Add(modalKey);
        }

        return insertedIds;
    }

    // ========================================================================
    // FINALIZATION
    // ========================================================================

    /// <summary>
    /// Bulk-update lastrundate = NOW() for weekly models.
    /// Replaces the per-model UPDATE in the WHILE loop.
    /// </summary>
    public async Task UpdateWeeklyLastRunDatesAsync(IEnumerable<int> modelIds, CancellationToken ct)
    {
        var idList = modelIds.ToList();
        if (idList.Count == 0) return;

        foreach (var batch in idList.Chunk(_config.DeleteBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                await conn.ExecuteAsync(
                    "UPDATE clientschedulemodel SET lastrundate = NOW() WHERE Id IN @Ids",
                    new { Ids = batch },
                    commandTimeout: 120);
            }, "UpdateWeeklyLastRunDates", ct);
        }

        _logger.LogInformation("Updated lastrundate (NOW) for {Count} weekly models", idList.Count);
    }

    /// <summary>
    /// Update lastrundate for monthly models to the 1st of next month.
    /// Monthly behavior is different from weekly — uses 1st-of-next-month.
    /// Mirrors: MonthlySchedular.sql line 241.
    /// </summary>
    public async Task UpdateMonthlyLastRunDatesAsync(
        IEnumerable<int> modelIds, DateTime monthStartDate, CancellationToken ct)
    {
        var idList = modelIds.ToList();
        if (idList.Count == 0) return;

        var nextMonth = monthStartDate.AddMonths(1);

        foreach (var batch in idList.Chunk(_config.DeleteBatchSize))
        {
            await _retryHandler.ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                await conn.ExecuteAsync(
                    "UPDATE clientschedulemodel SET lastrundate = @NextMonth WHERE Id IN @Ids",
                    new { NextMonth = nextMonth, Ids = batch },
                    commandTimeout: 120);
            }, "UpdateMonthlyLastRunDates", ct);
        }

        _logger.LogInformation("Updated lastrundate ({Date:yyyy-MM-dd}) for {Count} monthly models",
            nextMonth, idList.Count);
    }

    // ========================================================================
    // JOB TRACKING — StartEvent / CompleteEvent / logging
    // ========================================================================

    /// <summary>Write a message to logs.jobtracking for backward compatibility with monitoring.</summary>
    public async Task LogJobTrackingAsync(string message, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dbFactory.CreateConnectionAsync(ct);
            await conn.ExecuteAsync(
                "INSERT INTO logs.jobtracking VALUES (@Message)",
                new { Message = message },
                commandTimeout: 10);
        }
        catch (Exception ex)
        {
            // Don't let logging failures crash the job
            _logger.LogWarning(ex, "Failed to write to logs.jobtracking: {Message}", message);
        }
    }

    /// <summary>
    /// Call StartEvent function — concurrency guard.
    /// Returns true if the job can proceed, false if another instance is running.
    /// </summary>
    public async Task<bool> StartJobSessionAsync(
        string sessionId, DateTime startTime, string jobName, CancellationToken ct)
    {
        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var result = await conn.QuerySingleAsync<int>(
            "SELECT StartEvent(@SessionId, @StartTime, @JobName)",
            new { SessionId = sessionId, StartTime = startTime, JobName = jobName },
            commandTimeout: 30);
        return result != 0;
    }

    /// <summary>
    /// Call CompleteEvent procedure — marks the job session as finished.
    /// </summary>
    public async Task CompleteJobSessionAsync(
        string sessionId, DateTime endTime, int elapsedSeconds, CancellationToken ct)
    {
        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        await conn.ExecuteAsync(
            "CALL CompleteEvent(@SessionId, @EndTime, @Elapsed)",
            new { SessionId = sessionId, EndTime = endTime, Elapsed = elapsedSeconds },
            commandTimeout: 30);
    }

    /// <summary>
    /// Create a new database connection (exposed for slow-path operations
    /// that need multiple queries on the same connection for LAST_INSERT_ID).
    /// </summary>
    public async Task<MySqlConnection> CreateConnectionAsync(CancellationToken ct)
    {
        return await _dbFactory.CreateConnectionAsync(ct);
    }

    // ========================================================================
    // PRIVATE HELPERS
    // ========================================================================

    /// <summary>
    /// Build a multi-row INSERT VALUES statement for a batch of shifts.
    /// </summary>
    private async Task<int> InsertBatchAsync(
        MySqlConnection conn, ScheduleShift[] batch, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"INSERT INTO clientscheduleshift (
            ModalId, employeeid, Client_id, fromdate, todate, datetimein, datetimeout,
            duration, actualtimein, actualtimeout, actualduration, note,
            CreateDate, UpdateDate, IsActive, DeactivationDate, CreateUser_id, UpdateUser_id,
            timein, timeout, Employeetimecard_id,
            IsInMissing, IsOutMissing, IsLateIn, IsEarlyOut,
            IsInMissingAlertSent, IsOutMissingAlertSent, IsLateInAlertSent, IsEarlyOutAlertSent,
            IsLateInAlert, LateInDuration, IsLateOutAlert, LateOutDuration,
            IsCustomInAlert, IsCustomOutAlert, WorkOrderID,
            IsAutoClockOut, AutoClockOutSelectedValue, AutoClockOutHour, AutoClockOutMinutes,
            JobClassification_Id, IsTeamSchedule, GroupScheduleId,
            IsRounding, RoundUp, RoundDown, IsFlatRate, FlatRate,
            IsOpenSchedule, IsPublished,
            IsScheduleClockInRestrictionEnable, IsScheduleClockOutRestrictionEnable,
            IsScheduleDurationRestrictionEnable,
            ScheduleRestrictClockInBefore, ScheduleRestrictClockInAfter,
            ScheduleRestrictClockOutBefore, ScheduleRestrictClockOutAfter,
            ScheduleRestrictMinDuration, ScheduleRestrictMaxDuration,
            IsScheduleRestrictionEnable, CompanyID, ScheduleType, BreakDetailID,
            IsSuppressedScheduleRestriction, IsManagerApprovalEnabled,
            ScheduleScanType, UserNote
        ) VALUES ");

        var parameters = new DynamicParameters();

        for (int i = 0; i < batch.Length; i++)
        {
            if (i > 0) sb.Append(',');

            var s = batch[i];
            var p = $"p{i}_";

            sb.AppendLine($@"(
                @{p}ModalId, @{p}EmployeeId, @{p}ClientId, @{p}FromDate, @{p}ToDate,
                @{p}DateTimeIn, @{p}DateTimeOut, @{p}Duration, NULL, NULL, NULL, @{p}Note,
                @{p}CreateDate, @{p}UpdateDate, @{p}IsActive, NULL, @{p}CreateUserId, @{p}UpdateUserId,
                @{p}TimeIn, @{p}TimeOut, NULL,
                0, 0, 0, 0, 0, 0, 0, 0,
                @{p}IsLateInAlert, @{p}LateInDuration, @{p}IsLateOutAlert, @{p}LateOutDuration,
                @{p}IsCustomInAlert, @{p}IsCustomOutAlert, NULL,
                @{p}IsAutoClockOut, @{p}AutoClockOutSelectedValue, @{p}AutoClockOutHour, @{p}AutoClockOutMinutes,
                @{p}JobClassificationId, @{p}IsTeamSchedule, @{p}GroupScheduleId,
                @{p}IsRounding, @{p}RoundUp, @{p}RoundDown, @{p}IsFlatRate, @{p}FlatRate,
                @{p}IsOpenSchedule, @{p}IsPublished,
                @{p}IsClockInRestriction, @{p}IsClockOutRestriction, @{p}IsDurationRestriction,
                @{p}RestrictClockInBefore, @{p}RestrictClockInAfter,
                @{p}RestrictClockOutBefore, @{p}RestrictClockOutAfter,
                @{p}RestrictMinDuration, @{p}RestrictMaxDuration,
                @{p}IsRestrictionEnable, @{p}CompanyID, @{p}ScheduleType, NULLIF(@{p}BreakDetailID, 0),
                @{p}IsSuppressed, @{p}IsManagerApproval, @{p}ScanType, @{p}UserNote
            )");

            AddShiftParams(parameters, p, s);
        }

        return await conn.ExecuteAsync(sb.ToString(), parameters, commandTimeout: 120);
    }

    /// <summary>
    /// Build parameter object for a single-row INSERT (slow path).
    /// </summary>
    private static object BuildShiftParams(ScheduleShift s)
    {
        return new
        {
            s.ModalId,
            s.EmployeeId,
            ClientId        = s.Client_id,
            s.FromDate,
            s.ToDate,
            s.DateTimeIn,
            s.DateTimeOut,
            s.Duration,
            s.Note,
            s.CreateDate,
            s.UpdateDate,
            s.IsActive,
            CreateUserId    = s.CreateUser_id,
            UpdateUserId    = s.UpdateUser_id,
            s.TimeIn,
            s.TimeOut,
            s.IsLateInAlert,
            s.LateInDuration,
            s.IsLateOutAlert,
            s.LateOutDuration,
            s.IsCustomInAlert,
            s.IsCustomOutAlert,
            s.IsAutoClockOut,
            s.AutoClockOutSelectedValue,
            s.AutoClockOutHour,
            s.AutoClockOutMinutes,
            JobClassificationId   = s.JobClassification_Id,
            s.IsTeamSchedule,
            s.GroupScheduleId,
            s.IsRounding,
            s.RoundUp,
            s.RoundDown,
            s.IsFlatRate,
            s.FlatRate,
            s.IsOpenSchedule,
            s.IsPublished,
            IsClockInRestriction  = s.IsScheduleClockInRestrictionEnable,
            IsClockOutRestriction = s.IsScheduleClockOutRestrictionEnable,
            IsDurationRestriction = s.IsScheduleDurationRestrictionEnable,
            RestrictClockInBefore = s.ScheduleRestrictClockInBefore,
            RestrictClockInAfter  = s.ScheduleRestrictClockInAfter,
            RestrictClockOutBefore = s.ScheduleRestrictClockOutBefore,
            RestrictClockOutAfter  = s.ScheduleRestrictClockOutAfter,
            RestrictMinDuration   = s.ScheduleRestrictMinDuration,
            RestrictMaxDuration   = s.ScheduleRestrictMaxDuration,
            IsRestrictionEnable   = s.IsScheduleRestrictionEnable,
            s.CompanyID,
            s.ScheduleType,
            s.BreakDetailID,
            IsSuppressed          = s.IsSuppressedScheduleRestriction,
            IsManagerApproval     = s.IsManagerApprovalEnabled,
            ScanType              = s.ScheduleScanType,
            s.UserNote
        };
    }

    /// <summary>
    /// Add parameters for a single shift to a DynamicParameters collection (bulk insert).
    /// </summary>
    private static void AddShiftParams(DynamicParameters parameters, string prefix, ScheduleShift s)
    {
        parameters.Add($"{prefix}ModalId", s.ModalId);
        parameters.Add($"{prefix}EmployeeId", s.EmployeeId);
        parameters.Add($"{prefix}ClientId", s.Client_id);
        parameters.Add($"{prefix}FromDate", s.FromDate);
        parameters.Add($"{prefix}ToDate", s.ToDate);
        parameters.Add($"{prefix}DateTimeIn", s.DateTimeIn);
        parameters.Add($"{prefix}DateTimeOut", s.DateTimeOut);
        parameters.Add($"{prefix}Duration", s.Duration);
        parameters.Add($"{prefix}Note", s.Note);
        parameters.Add($"{prefix}CreateDate", s.CreateDate);
        parameters.Add($"{prefix}UpdateDate", s.UpdateDate);
        parameters.Add($"{prefix}IsActive", s.IsActive);
        parameters.Add($"{prefix}CreateUserId", s.CreateUser_id);
        parameters.Add($"{prefix}UpdateUserId", s.UpdateUser_id);
        parameters.Add($"{prefix}TimeIn", s.TimeIn);
        parameters.Add($"{prefix}TimeOut", s.TimeOut);
        parameters.Add($"{prefix}IsLateInAlert", s.IsLateInAlert);
        parameters.Add($"{prefix}LateInDuration", s.LateInDuration);
        parameters.Add($"{prefix}IsLateOutAlert", s.IsLateOutAlert);
        parameters.Add($"{prefix}LateOutDuration", s.LateOutDuration);
        parameters.Add($"{prefix}IsCustomInAlert", s.IsCustomInAlert);
        parameters.Add($"{prefix}IsCustomOutAlert", s.IsCustomOutAlert);
        parameters.Add($"{prefix}IsAutoClockOut", s.IsAutoClockOut);
        parameters.Add($"{prefix}AutoClockOutSelectedValue", s.AutoClockOutSelectedValue);
        parameters.Add($"{prefix}AutoClockOutHour", s.AutoClockOutHour);
        parameters.Add($"{prefix}AutoClockOutMinutes", s.AutoClockOutMinutes);
        parameters.Add($"{prefix}JobClassificationId", s.JobClassification_Id);
        parameters.Add($"{prefix}IsTeamSchedule", s.IsTeamSchedule);
        parameters.Add($"{prefix}GroupScheduleId", s.GroupScheduleId);
        parameters.Add($"{prefix}IsRounding", s.IsRounding);
        parameters.Add($"{prefix}RoundUp", s.RoundUp);
        parameters.Add($"{prefix}RoundDown", s.RoundDown);
        parameters.Add($"{prefix}IsFlatRate", s.IsFlatRate);
        parameters.Add($"{prefix}FlatRate", s.FlatRate);
        parameters.Add($"{prefix}IsOpenSchedule", s.IsOpenSchedule);
        parameters.Add($"{prefix}IsPublished", s.IsPublished);
        parameters.Add($"{prefix}IsClockInRestriction", s.IsScheduleClockInRestrictionEnable);
        parameters.Add($"{prefix}IsClockOutRestriction", s.IsScheduleClockOutRestrictionEnable);
        parameters.Add($"{prefix}IsDurationRestriction", s.IsScheduleDurationRestrictionEnable);
        parameters.Add($"{prefix}RestrictClockInBefore", s.ScheduleRestrictClockInBefore);
        parameters.Add($"{prefix}RestrictClockInAfter", s.ScheduleRestrictClockInAfter);
        parameters.Add($"{prefix}RestrictClockOutBefore", s.ScheduleRestrictClockOutBefore);
        parameters.Add($"{prefix}RestrictClockOutAfter", s.ScheduleRestrictClockOutAfter);
        parameters.Add($"{prefix}RestrictMinDuration", s.ScheduleRestrictMinDuration);
        parameters.Add($"{prefix}RestrictMaxDuration", s.ScheduleRestrictMaxDuration);
        parameters.Add($"{prefix}IsRestrictionEnable", s.IsScheduleRestrictionEnable);
        parameters.Add($"{prefix}CompanyID", s.CompanyID);
        parameters.Add($"{prefix}ScheduleType", s.ScheduleType);
        parameters.Add($"{prefix}BreakDetailID", s.BreakDetailID);
        parameters.Add($"{prefix}IsSuppressed", s.IsSuppressedScheduleRestriction);
        parameters.Add($"{prefix}IsManagerApproval", s.IsManagerApprovalEnabled);
        parameters.Add($"{prefix}ScanType", s.ScheduleScanType);
        parameters.Add($"{prefix}UserNote", s.UserNote);
    }

    // ========================================================================
    // AUDIT LOG & OVERLAP DETECTION
    // ========================================================================

    /// <summary>
    /// Ensure the audit and conflict tables exist. Called once at job startup.
    /// Uses CREATE TABLE IF NOT EXISTS so it's safe to call every run.
    /// </summary>
    public async Task EnsureAuditTablesAsync(CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS job_shift_audit_log (
              Id               BIGINT AUTO_INCREMENT PRIMARY KEY,
              RunId            VARCHAR(36) NOT NULL,
              RunDate          DATETIME NOT NULL,
              ModalId          INT NOT NULL,
              ShiftId          BIGINT NULL,
              EmployeeId       INT NOT NULL,
              ClientId         INT NOT NULL,
              DateTimeIn       DATETIME NOT NULL,
              DateTimeOut      DATETIME NOT NULL,
              Result           VARCHAR(20) NOT NULL,
              ErrorDescription VARCHAR(500) NULL,
              ModelType        VARCHAR(10) NOT NULL,
              RecurringPattern VARCHAR(50) NOT NULL,
              CreatedAt        DATETIME NOT NULL DEFAULT NOW(),
              INDEX idx_audit_runid (RunId),
              INDEX idx_audit_created (CreatedAt),
              INDEX idx_audit_result (Result),
              INDEX idx_audit_employee (EmployeeId, DateTimeIn)
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS job_shift_conflicts (
              Id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
              RunId                VARCHAR(36) NOT NULL,
              ModalId              INT NOT NULL,
              EmployeeId           INT NOT NULL,
              ClientId             INT NOT NULL,
              DateTimeIn           DATETIME NOT NULL,
              DateTimeOut          DATETIME NOT NULL,
              ConflictingShiftId   BIGINT NULL,
              ConflictingModalId   INT NULL,
              ConflictingClientId  INT NOT NULL,
              ConflictDateTimeIn   DATETIME NOT NULL,
              ConflictDateTimeOut  DATETIME NOT NULL,
              DetectedAt           DATETIME NOT NULL DEFAULT NOW(),
              INDEX idx_conflict_runid (RunId),
              INDEX idx_conflict_employee (EmployeeId),
              INDEX idx_conflict_detected (DetectedAt)
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS job_scheduler_run (
              RunId                VARCHAR(36) NOT NULL PRIMARY KEY,
              StartedAt            DATETIME NOT NULL,
              CompletedAt          DATETIME NULL,
              DurationSeconds      INT NULL,
              Status               VARCHAR(20) NOT NULL DEFAULT 'Running',
              WeeklyModelsLoaded   INT NOT NULL DEFAULT 0,
              RecordsConsidered    INT NOT NULL DEFAULT 0,
              ShiftsCreated        INT NOT NULL DEFAULT 0,
              ShiftsSkipped        INT NOT NULL DEFAULT 0,
              OrphanedDeleted      INT NOT NULL DEFAULT 0,
              ResetDeleted         INT NOT NULL DEFAULT 0,
              AuditEntriesCount    INT NOT NULL DEFAULT 0,
              ConflictsCount       INT NOT NULL DEFAULT 0,
              ErrorMessage         VARCHAR(500) NULL,
              INDEX idx_run_started (StartedAt)
            ) ENGINE=InnoDB;";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        await conn.ExecuteAsync(sql, commandTimeout: 60);
    }

    /// <summary>
    /// Insert a run summary row at job start. Use RunId as primary key for drill-down from portal.
    /// </summary>
    public async Task InsertRunSummaryAsync(string runId, DateTime startedAt, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO job_scheduler_run
            (RunId, StartedAt, Status, WeeklyModelsLoaded, RecordsConsidered, ShiftsCreated,
             ShiftsSkipped, OrphanedDeleted, ResetDeleted, AuditEntriesCount, ConflictsCount)
            VALUES (@RunId, @StartedAt, 'Running', 0, 0, 0, 0, 0, 0, 0, 0)";

        try
        {
            await using var conn = await _dbFactory.CreateConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new { RunId = runId, StartedAt = startedAt }, commandTimeout: 10);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to insert run summary for RunId={RunId}", runId);
        }
    }

    /// <summary>
    /// Update the run summary row with final counts and status. Called at job end for portal drill-down.
    /// </summary>
    public async Task UpdateRunSummaryAsync(
        string runId,
        DateTime completedAt,
        int durationSeconds,
        string status,
        int weeklyModelsLoaded,
        int recordsConsidered,
        int shiftsCreated,
        int shiftsSkipped,
        int orphanedDeleted,
        int resetDeleted,
        int auditEntriesCount,
        int conflictsCount,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = @"
            UPDATE job_scheduler_run
            SET CompletedAt = @CompletedAt,
                DurationSeconds = @DurationSeconds,
                Status = @Status,
                WeeklyModelsLoaded = @WeeklyModelsLoaded,
                RecordsConsidered = @RecordsConsidered,
                ShiftsCreated = @ShiftsCreated,
                ShiftsSkipped = @ShiftsSkipped,
                OrphanedDeleted = @OrphanedDeleted,
                ResetDeleted = @ResetDeleted,
                AuditEntriesCount = @AuditEntriesCount,
                ConflictsCount = @ConflictsCount,
                ErrorMessage = @ErrorMessage
            WHERE RunId = @RunId";

        try
        {
            await using var conn = await _dbFactory.CreateConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new
            {
                RunId = runId,
                CompletedAt = completedAt,
                DurationSeconds = durationSeconds,
                Status = status,
                WeeklyModelsLoaded = weeklyModelsLoaded,
                RecordsConsidered = recordsConsidered,
                ShiftsCreated = shiftsCreated,
                ShiftsSkipped = shiftsSkipped,
                OrphanedDeleted = orphanedDeleted,
                ResetDeleted = resetDeleted,
                AuditEntriesCount = auditEntriesCount,
                ConflictsCount = conflictsCount,
                ErrorMessage = errorMessage
            }, commandTimeout: 10);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update run summary for RunId={RunId}", runId);
        }
    }

    /// <summary>
    /// Bulk-load employee shift intervals for overlap detection.
    /// Returns tuples that the OverlapDetector can consume.
    /// Only loads shifts with EmployeeId > 0 (skip unassigned).
    /// </summary>
    public async Task<List<(int EmployeeId, DateTime DateTimeIn, DateTime DateTimeOut,
        int ClientId, long ShiftId, int ModalId)>> LoadEmployeeShiftIntervalsAsync(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        const string sql = @"
            SELECT employeeid AS EmployeeId,
                   datetimein AS DateTimeIn,
                   datetimeout AS DateTimeOut,
                   Client_id AS ClientId,
                   Id AS ShiftId,
                   ModalId
            FROM   clientscheduleshift
            WHERE  employeeid > 0
              AND  datetimein >= @StartDate
              AND  datetimein <= @EndDate";

        await using var conn = await _dbFactory.CreateConnectionAsync(ct);
        var rows = await conn.QueryAsync<(int EmployeeId, DateTime DateTimeIn, DateTime DateTimeOut,
            int ClientId, long ShiftId, int ModalId)>(
            sql,
            new { StartDate = startDate.Date, EndDate = endDate.Date.AddDays(1) },
            commandTimeout: 300);

        return rows.ToList();
    }

    /// <summary>
    /// Bulk insert audit log entries in batches.
    /// </summary>
    public async Task BulkInsertAuditLogAsync(
        List<Models.ShiftAuditEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        const int batchSize = 1000;

        foreach (var batch in entries.Chunk(batchSize))
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(@"INSERT INTO job_shift_audit_log
                (RunId, RunDate, ModalId, ShiftId, EmployeeId, ClientId,
                 DateTimeIn, DateTimeOut, Result, ErrorDescription,
                 ModelType, RecurringPattern, CreatedAt) VALUES ");

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var p = $"p{i}";
                sb.Append($"(@{p}RunId, @{p}RunDate, @{p}ModalId, @{p}ShiftId, @{p}EmpId, @{p}ClientId, " +
                          $"@{p}DtIn, @{p}DtOut, @{p}Result, @{p}ErrDesc, @{p}ModelType, @{p}Pattern, NOW())");

                var e = batch[i];
                parameters.Add($"{p}RunId", e.RunId);
                parameters.Add($"{p}RunDate", e.RunDate);
                parameters.Add($"{p}ModalId", e.ModalId);
                parameters.Add($"{p}ShiftId", e.ShiftId);
                parameters.Add($"{p}EmpId", e.EmployeeId);
                parameters.Add($"{p}ClientId", e.ClientId);
                parameters.Add($"{p}DtIn", e.DateTimeIn);
                parameters.Add($"{p}DtOut", e.DateTimeOut);
                parameters.Add($"{p}Result", e.Result);
                parameters.Add($"{p}ErrDesc", e.ErrorDescription);
                parameters.Add($"{p}ModelType", e.ModelType);
                parameters.Add($"{p}Pattern", e.RecurringPattern);
            }

            try
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                await conn.ExecuteAsync(sb.ToString(), parameters, commandTimeout: 120);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to insert audit log batch of {Count} entries", batch.Length);
            }
        }
    }

    /// <summary>
    /// Bulk insert conflict rows in batches.
    /// </summary>
    public async Task BulkInsertConflictsAsync(
        List<Models.ShiftConflict> conflicts, CancellationToken ct)
    {
        if (conflicts.Count == 0) return;

        const int batchSize = 1000;

        foreach (var batch in conflicts.Chunk(batchSize))
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(@"INSERT INTO job_shift_conflicts
                (RunId, ModalId, EmployeeId, ClientId, DateTimeIn, DateTimeOut,
                 ConflictingShiftId, ConflictingModalId, ConflictingClientId,
                 ConflictDateTimeIn, ConflictDateTimeOut, DetectedAt) VALUES ");

            var parameters = new DynamicParameters();
            for (int i = 0; i < batch.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var p = $"p{i}";
                sb.Append($"(@{p}RunId, @{p}ModalId, @{p}EmpId, @{p}ClientId, @{p}DtIn, @{p}DtOut, " +
                          $"@{p}CShiftId, @{p}CModalId, @{p}CClientId, @{p}CDtIn, @{p}CDtOut, NOW())");

                var c = batch[i];
                parameters.Add($"{p}RunId", c.RunId);
                parameters.Add($"{p}ModalId", c.ModalId);
                parameters.Add($"{p}EmpId", c.EmployeeId);
                parameters.Add($"{p}ClientId", c.ClientId);
                parameters.Add($"{p}DtIn", c.DateTimeIn);
                parameters.Add($"{p}DtOut", c.DateTimeOut);
                parameters.Add($"{p}CShiftId", c.ConflictingShiftId);
                parameters.Add($"{p}CModalId", c.ConflictingModalId);
                parameters.Add($"{p}CClientId", c.ConflictingClientId);
                parameters.Add($"{p}CDtIn", c.ConflictDateTimeIn);
                parameters.Add($"{p}CDtOut", c.ConflictDateTimeOut);
            }

            try
            {
                await using var conn = await _dbFactory.CreateConnectionAsync(ct);
                await conn.ExecuteAsync(sb.ToString(), parameters, commandTimeout: 120);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to insert conflict batch of {Count} entries", batch.Length);
            }
        }
    }

    /// <summary>
    /// Delete audit log, conflict, and run summary rows older than the retention period.
    /// </summary>
    public async Task CleanupAuditTablesAsync(int retentionDays, CancellationToken ct)
    {
        const string sql = @"
            DELETE FROM job_shift_audit_log WHERE CreatedAt < DATE_SUB(NOW(), INTERVAL @Days DAY);
            DELETE FROM job_shift_conflicts WHERE DetectedAt < DATE_SUB(NOW(), INTERVAL @Days DAY);
            DELETE FROM job_scheduler_run WHERE StartedAt < DATE_SUB(NOW(), INTERVAL @Days DAY);";

        try
        {
            await using var conn = await _dbFactory.CreateConnectionAsync(ct);
            await conn.ExecuteAsync(sql, new { Days = retentionDays }, commandTimeout: 120);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup audit tables (retention: {Days} days)", retentionDays);
        }
    }
}
