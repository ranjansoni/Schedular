namespace JMScheduler.Core.Models;

/// <summary>
/// Represents a shift to be inserted into clientscheduleshift.
/// Built in-memory from a ScheduleModel + a target date, then bulk-inserted or individually inserted.
///
/// Two factory methods:
///   FromModel()          — for individual (non-group) shifts (weekly or monthly)
///   FromModelForGroup()  — for group shifts where GroupScheduleId is overridden with a new cloned ID
/// </summary>
public sealed class ScheduleShift
{
    // ---- Core identifiers ----
    public int ModalId { get; set; }
    public int EmployeeId { get; set; }
    public int Client_id { get; set; }

    // ---- Date/time ----
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime DateTimeIn { get; set; }
    public DateTime DateTimeOut { get; set; }
    public decimal Duration { get; set; }

    // ---- Metadata ----
    /// <summary>"Scheduled Event" for weekly, "Schedule Event Monthly" for monthly.</summary>
    public string Note { get; set; } = "Scheduled Event";
    public DateTime CreateDate { get; set; }
    public DateTime UpdateDate { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreateUser_id { get; set; } = 41;
    public int UpdateUser_id { get; set; } = 41;
    public string TimeIn { get; set; } = string.Empty;
    public string TimeOut { get; set; } = string.Empty;

    // ---- Alert/status flags (all default false for new shifts) ----
    public bool IsInMissing { get; set; }
    public bool IsOutMissing { get; set; }
    public bool IsLateIn { get; set; }
    public bool IsEarlyOut { get; set; }
    public bool IsInMissingAlertSent { get; set; }
    public bool IsOutMissingAlertSent { get; set; }
    public bool IsLateInAlertSent { get; set; }
    public bool IsEarlyOutAlertSent { get; set; }

    // ---- Copied from model ----
    public bool IsLateInAlert { get; set; }
    public int LateInDuration { get; set; }
    public bool IsLateOutAlert { get; set; }
    public int LateOutDuration { get; set; }
    public bool IsCustomInAlert { get; set; }
    public bool IsCustomOutAlert { get; set; }
    public bool IsAutoClockOut { get; set; }
    public string? AutoClockOutSelectedValue { get; set; }
    public double AutoClockOutHour { get; set; }
    public int AutoClockOutMinutes { get; set; }
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

    /// <summary>
    /// Build a ScheduleShift from a model template and a target schedule date.
    /// Used for individual (non-group) shifts.
    /// </summary>
    /// <param name="model">The schedule model template.</param>
    /// <param name="scheduleDate">The target date to create the shift for.</param>
    /// <param name="note">Note text: "Scheduled Event" for weekly, "Schedule Event Monthly" for monthly.</param>
    public static ScheduleShift FromModel(ScheduleModel model, DateTime scheduleDate, string note = "Scheduled Event")
    {
        var (dateTimeIn, dateTimeOut) = CalculateDateTimes(model, scheduleDate);
        return BuildShift(model, dateTimeIn, dateTimeOut, model.GroupScheduleId, note);
    }

    /// <summary>
    /// Build a ScheduleShift for a group schedule, using a newly cloned GroupScheduleId.
    /// </summary>
    /// <param name="model">The schedule model template (one of potentially many in the group).</param>
    /// <param name="scheduleDate">The target date.</param>
    /// <param name="newGroupScheduleId">The newly inserted groupschedule.Id to use.</param>
    /// <param name="note">Note text.</param>
    public static ScheduleShift FromModelForGroup(
        ScheduleModel model, DateTime scheduleDate, int newGroupScheduleId, string note = "Scheduled Event")
    {
        var (dateTimeIn, dateTimeOut) = CalculateDateTimes(model, scheduleDate);
        return BuildShift(model, dateTimeIn, dateTimeOut, newGroupScheduleId, note);
    }

    /// <summary>
    /// Build the duplicate-check key for this shift: "ClientId|EmployeeId|DateTimeIn|DateTimeOut".
    /// Must match the format used by ScheduleRepository.LoadExistingShiftKeysAsync().
    /// </summary>
    public string GetDuplicateKey()
    {
        return $"{Client_id}|{EmployeeId}|{DateTimeIn:yyyy-MM-dd HH:mm}|{DateTimeOut:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Build a model-aware duplicate key: "ModalId|ClientId|EmployeeId|DateTimeIn|DateTimeOut".
    /// Used for ScheduleType=1 (OpenWithAllClaim) where multiple models may legitimately
    /// produce shifts with the same Client/Employee/DateTime, but the same model should
    /// NOT create duplicates across runs.
    /// Must match the format used by ScheduleRepository.LoadExistingModalShiftKeysAsync().
    /// </summary>
    public string GetModalDuplicateKey()
    {
        return $"{ModalId}|{Client_id}|{EmployeeId}|{DateTimeIn:yyyy-MM-dd HH:mm}|{DateTimeOut:yyyy-MM-dd HH:mm}";
    }

    // ---- Private helpers ----

    /// <summary>
    /// Calculate DateTimeIn and DateTimeOut from a model and target date.
    /// Handles multi-day (overnight) shifts where DaySpan > 0.
    /// </summary>
    private static (DateTime dateTimeIn, DateTime dateTimeOut) CalculateDateTimes(
        ScheduleModel model, DateTime scheduleDate)
    {
        var dateTimeIn = scheduleDate.Date + model.FromDate.TimeOfDay;
        var dateTimeOut = model.DaySpan > 0
            ? scheduleDate.Date.AddDays(model.DaySpan) + model.ToDate.TimeOfDay
            : scheduleDate.Date + model.ToDate.TimeOfDay;

        return (dateTimeIn, dateTimeOut);
    }

    /// <summary>
    /// Common builder that maps all model properties to a new shift instance.
    /// </summary>
    private static ScheduleShift BuildShift(
        ScheduleModel model, DateTime dateTimeIn, DateTime dateTimeOut, int groupScheduleId, string note)
    {
        var now = DateTime.Now;

        return new ScheduleShift
        {
            ModalId                              = model.Id,
            EmployeeId                           = model.EmployeeId,
            Client_id                            = model.Client_id,
            FromDate                             = dateTimeIn.Date,
            ToDate                               = dateTimeOut.Date,
            DateTimeIn                           = dateTimeIn,
            DateTimeOut                          = dateTimeOut,
            Duration                             = model.Duration,
            Note                                 = note,
            CreateDate                           = now,
            UpdateDate                           = now,
            IsActive                             = true,
            CreateUser_id                        = 41,
            UpdateUser_id                        = 41,
            TimeIn                               = model.TimeIn,
            TimeOut                              = model.TimeOut,
            IsLateInAlert                        = model.IsLateInAlert,
            LateInDuration                       = model.LateInDuration,
            IsLateOutAlert                       = model.IsLateOutAlert,
            LateOutDuration                      = model.LateOutDuration,
            IsCustomInAlert                      = model.IsCustomInAlert,
            IsCustomOutAlert                     = model.IsCustomOutAlert,
            IsAutoClockOut                       = model.IsAutoClockOut,
            AutoClockOutSelectedValue            = model.AutoClockOutSelectedValue,
            AutoClockOutHour                     = model.AutoClockOutHour,
            AutoClockOutMinutes                  = model.AutoClockOutMinutes,
            JobClassification_Id                 = model.JobClassification_Id,
            IsTeamSchedule                       = model.IsTeamSchedule,
            GroupScheduleId                      = groupScheduleId,
            IsRounding                           = model.IsRounding,
            RoundUp                              = model.RoundUp,
            RoundDown                            = model.RoundDown,
            IsFlatRate                           = model.IsFlatRate,
            FlatRate                             = model.FlatRate,
            IsOpenSchedule                       = model.IsOpenSchedule,
            IsPublished                          = model.IsPublished,
            IsScheduleClockInRestrictionEnable   = model.IsScheduleClockInRestrictionEnable,
            IsScheduleClockOutRestrictionEnable  = model.IsScheduleClockOutRestrictionEnable,
            IsScheduleDurationRestrictionEnable  = model.IsScheduleDurationRestrictionEnable,
            ScheduleRestrictClockInBefore        = model.ScheduleRestrictClockInBefore,
            ScheduleRestrictClockInAfter         = model.ScheduleRestrictClockInAfter,
            ScheduleRestrictClockOutBefore       = model.ScheduleRestrictClockOutBefore,
            ScheduleRestrictClockOutAfter        = model.ScheduleRestrictClockOutAfter,
            ScheduleRestrictMinDuration          = model.ScheduleRestrictMinDuration,
            ScheduleRestrictMaxDuration          = model.ScheduleRestrictMaxDuration,
            IsScheduleRestrictionEnable          = model.IsScheduleRestrictionEnable,
            CompanyID                            = model.CompanyID,
            ScheduleType                         = model.ScheduleType,
            BreakDetailID                        = model.BreakDetailID,
            IsSuppressedScheduleRestriction      = model.IsSuppressedScheduleRestriction,
            IsManagerApprovalEnabled             = model.IsManagerApprovalEnabled,
            ScheduleScanType                     = model.ScheduleScanType,
            UserNote                             = model.UserNote,
        };
    }
}
