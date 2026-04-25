namespace JMScheduler.Core.Configuration;

/// <summary>
/// Configuration settings for the scheduler job, bound from appsettings.json "Scheduler" section.
/// Values below are fallback defaults only; appsettings.json (and JM_ env vars) override them.
/// </summary>
public sealed class SchedulerConfig
{
    public const string SectionName = "Scheduler";

    // ---- Advance window ----

    /// <summary>Number of days in advance to generate weekly/multi-week shifts (e.g. 45 or 90).</summary>
    public int AdvanceDays { get; set; } = 45;

    /// <summary>Number of months ahead to generate monthly shifts. The original SP always used 3 (current + 2).</summary>
    public int MonthlyMonthsAhead { get; set; } = 3;

    // ---- Batch sizes ----

    /// <summary>How many rows to delete per batch during cleanup phase.</summary>
    public int DeleteBatchSize { get; set; } = 5000;

    /// <summary>How many shift rows to INSERT in a single multi-row statement (fast path).</summary>
    public int InsertBatchSize { get; set; } = 1000;

    // ---- Throttling ----

    /// <summary>Milliseconds to sleep between batches to reduce lock contention with web/mobile/alert traffic.</summary>
    public int SleepBetweenBatchesMs { get; set; } = 100;

    // ---- Deadlock retry ----

    /// <summary>Maximum number of retries on deadlock (MySQL error 1213) or lock wait timeout (1205).</summary>
    public int MaxDeadlockRetries { get; set; } = 5;

    /// <summary>Base delay in ms for exponential backoff on deadlock retry. Actual delay = base * 2^(attempt-1).</summary>
    public int DeadlockRetryBaseDelayMs { get; set; } = 200;

    // ---- Session settings (match original SP) ----

    /// <summary>MySQL session time zone. Must match the original SP setting.</summary>
    public string TimeZone { get; set; } = "US/Eastern";

    // ---- History cleanup ----

    /// <summary>Number of days to retain in job_clientschedulefunctiondataHistory. Original SP used 120.</summary>
    public int HistoryRetentionDays { get; set; } = 120;

    // ---- Audit / Overlap ----

    /// <summary>Number of days to retain audit log and conflict rows. Default 3 days.</summary>
    public int AuditRetentionDays { get; set; } = 3;
}
