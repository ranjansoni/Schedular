namespace JMScheduler.Core.Models;

/// <summary>
/// A single row in the job_shift_audit_log table.
/// Tracks every shift attempt (created, duplicate, overlap, error) for each run.
/// </summary>
public sealed class ShiftAuditEntry
{
    public string RunId { get; set; } = string.Empty;
    public DateTime RunDate { get; set; }
    public int ModalId { get; set; }
    public long? ShiftId { get; set; }
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public DateTime DateTimeIn { get; set; }
    public DateTime DateTimeOut { get; set; }

    /// <summary>'Created', 'Duplicate', 'Overlap', 'Error'</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Human-readable description when Result is not 'Created'.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>'Weekly' or 'Monthly'</summary>
    public string ModelType { get; set; } = string.Empty;

    /// <summary>e.g. "Every week", "Every 2 weeks", "1st Monday", "Last Friday"</summary>
    public string RecurringPattern { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- Factory helpers ----

    public static ShiftAuditEntry Created(string runId, DateTime runDate, ScheduleShift shift,
        ScheduleModel model, string modelType, string recurringPattern)
    {
        return new ShiftAuditEntry
        {
            RunId = runId,
            RunDate = runDate,
            ModalId = model.Id,
            ShiftId = null, // filled after bulk insert (IDs not individually tracked)
            EmployeeId = model.EmployeeId,
            ClientId = model.Client_id,
            DateTimeIn = shift.DateTimeIn,
            DateTimeOut = shift.DateTimeOut,
            Result = "Created",
            ModelType = modelType,
            RecurringPattern = recurringPattern
        };
    }

    public static ShiftAuditEntry Duplicate(string runId, DateTime runDate, ScheduleShift shift,
        ScheduleModel model, string modelType, string recurringPattern)
    {
        return new ShiftAuditEntry
        {
            RunId = runId,
            RunDate = runDate,
            ModalId = model.Id,
            EmployeeId = model.EmployeeId,
            ClientId = model.Client_id,
            DateTimeIn = shift.DateTimeIn,
            DateTimeOut = shift.DateTimeOut,
            Result = "Duplicate",
            ErrorDescription = "Shift already exists for this model/employee/date",
            ModelType = modelType,
            RecurringPattern = recurringPattern
        };
    }

    public static ShiftAuditEntry Overlap(string runId, DateTime runDate, ScheduleShift shift,
        ScheduleModel model, string modelType, string recurringPattern, string conflictDescription)
    {
        return new ShiftAuditEntry
        {
            RunId = runId,
            RunDate = runDate,
            ModalId = model.Id,
            EmployeeId = model.EmployeeId,
            ClientId = model.Client_id,
            DateTimeIn = shift.DateTimeIn,
            DateTimeOut = shift.DateTimeOut,
            Result = "Overlap",
            ErrorDescription = conflictDescription,
            ModelType = modelType,
            RecurringPattern = recurringPattern
        };
    }

    // ---- Recurring pattern builder ----

    /// <summary>Build a human-readable recurrence string from model properties.</summary>
    public static string BuildRecurringPattern(ScheduleModel model)
    {
        if (model.RecurringType == 0)
        {
            // Weekly / multi-week
            return model.RecurringOn <= 1
                ? "Every week"
                : $"Every {model.RecurringOn} weeks";
        }

        // Monthly
        string dayName = GetScheduledDayName(model);
        return model.MonthlyRecurringType switch
        {
            0 => $"1st {dayName}",
            1 => $"2nd {dayName}",
            2 => $"3rd {dayName}",
            3 => $"4th {dayName}",
            4 => $"Last {dayName}",
            _ => $"Monthly ({model.MonthlyRecurringType})"
        };
    }

    private static string GetScheduledDayName(ScheduleModel model)
    {
        if (model.Monday)    return "Monday";
        if (model.Tuesday)   return "Tuesday";
        if (model.Wednesday) return "Wednesday";
        if (model.Thursday)  return "Thursday";
        if (model.Friday)    return "Friday";
        if (model.Saturday)  return "Saturday";
        if (model.Sunday)    return "Sunday";
        return "Unknown";
    }
}
