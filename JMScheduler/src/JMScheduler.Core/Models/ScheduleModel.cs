namespace JMScheduler.Core.Models;

/// <summary>
/// Represents a row from clientschedulemodel — the "template" that defines
/// a recurring shift for an employee at a client site.
///
/// RecurringType determines which processing path is used:
///   0 = Weekly / Multi-week (processed by WeeklyScheduleService)
///   1 = Monthly (processed by MonthlyScheduleService)
///
/// RecurringOn (for weekly) determines the week interval:
///   1 = Every week, 2 = Every 2 weeks (biweekly), N = Every N weeks
///
/// MonthlyRecurringType (for monthly) determines which week occurrence:
///   0 = 1st, 1 = 2nd, 2 = 3rd, 3 = 4th (with overflow → last)
/// </summary>
public sealed class ScheduleModel
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int Client_id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime LastRunDate { get; set; }
    public string TimeIn { get; set; } = string.Empty;
    public string TimeOut { get; set; } = string.Empty;
    public decimal Duration { get; set; }

    // ---- Recurrence ----
    /// <summary>0 = Weekly/Multi-week, 1 = Monthly</summary>
    public int RecurringType { get; set; }

    /// <summary>Week interval for RecurringType=0: 1=weekly, 2=biweekly, N=every N weeks.</summary>
    public int RecurringOn { get; set; }

    /// <summary>For RecurringType=1: which occurrence of the weekday in the month (0=1st, 1=2nd, 2=3rd, 3=4th).</summary>
    public int MonthlyRecurringType { get; set; }

    /// <summary>True if model has been edited/reset and needs schedule regeneration.</summary>
    public bool IsModelReset { get; set; }

    /// <summary>Whether the model is active.</summary>
    public bool IsActive { get; set; }

    public bool NoEndDate { get; set; }

    // ---- Day-of-week flags ----
    public bool Sunday { get; set; }
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }

    // ---- Alert settings ----
    public bool IsLateInAlert { get; set; }
    public int LateInDuration { get; set; }
    public bool IsLateOutAlert { get; set; }
    public int LateOutDuration { get; set; }
    public bool IsCustomInAlert { get; set; }
    public bool IsCustomOutAlert { get; set; }

    // ---- Auto clock-out ----
    public bool IsAutoClockOut { get; set; }
    public string? AutoClockOutSelectedValue { get; set; }
    public double AutoClockOutHour { get; set; }
    public int AutoClockOutMinutes { get; set; }

    // ---- Classification & grouping ----
    public int JobClassification_Id { get; set; }
    public bool IsTeamSchedule { get; set; }
    public int GroupScheduleId { get; set; }
    public bool IsRounding { get; set; }
    public double RoundUp { get; set; }
    public double RoundDown { get; set; }
    public bool IsFlatRate { get; set; }
    public double FlatRate { get; set; }
    public bool IsOpenSchedule { get; set; }
    public bool IsPublished { get; set; }

    // ---- Schedule restrictions ----
    public bool IsScheduleClockInRestrictionEnable { get; set; }
    public bool IsScheduleClockOutRestrictionEnable { get; set; }
    public bool IsScheduleDurationRestrictionEnable { get; set; }
    public double ScheduleRestrictClockInBefore { get; set; }
    public double ScheduleRestrictClockInAfter { get; set; }
    public double ScheduleRestrictClockOutBefore { get; set; }
    public double ScheduleRestrictClockOutAfter { get; set; }
    public double ScheduleRestrictMinDuration { get; set; }
    public double ScheduleRestrictMaxDuration { get; set; }
    public double IsScheduleRestrictionEnable { get; set; }

    public int CompanyID { get; set; }
    public int ScheduleType { get; set; }
    public int BreakDetailID { get; set; }
    public bool IsSuppressedScheduleRestriction { get; set; }
    public bool IsManagerApprovalEnabled { get; set; }
    public int ScheduleScanType { get; set; }
    public string? UserNote { get; set; }

    // ---- Computed helpers ----

    /// <summary>
    /// Returns true if this model is scheduled for the given day of week.
    /// Replaces the 7x copy-pasted IF blocks in ProcessScheduleModal (420 lines → 1 call).
    /// </summary>
    public bool IsScheduledForDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday    => Sunday,
        DayOfWeek.Monday    => Monday,
        DayOfWeek.Tuesday   => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday  => Thursday,
        DayOfWeek.Friday    => Friday,
        DayOfWeek.Saturday  => Saturday,
        _ => false
    };

    /// <summary>True if this model belongs to a group schedule.</summary>
    public bool HasGroupSchedule => GroupScheduleId > 0;

    /// <summary>Number of days the shift spans (0 = same-day, 1+ = overnight/multi-day).</summary>
    public int DaySpan => (ToDate.Date - FromDate.Date).Days;

    /// <summary>True if EndDate is the sentinel value meaning "no end date".</summary>
    public bool HasNoEndDate => EndDate.Year <= 1;

    /// <summary>True if LastRunDate is the sentinel value meaning "never run".</summary>
    public bool HasNeverRun => LastRunDate.Year <= 1 || LastRunDate == default;

    /// <summary>True if this model needs multi-week date calculation (biweekly/triweekly/etc.).</summary>
    public bool IsMultiWeek => RecurringType == 0 && RecurringOn > 1;
}
