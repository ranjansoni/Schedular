namespace JMScheduler.Job.Models;

/// <summary>
/// A single row in the job_shift_conflicts table.
/// Records when a shift was blocked due to an employee time overlap at a different location.
/// </summary>
public sealed class ShiftConflict
{
    public string RunId { get; set; } = string.Empty;

    // ---- Blocked shift details ----
    public int ModalId { get; set; }
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public DateTime DateTimeIn { get; set; }
    public DateTime DateTimeOut { get; set; }

    // ---- Conflicting (existing) shift details ----
    public long? ConflictingShiftId { get; set; }
    public int? ConflictingModalId { get; set; }
    public int ConflictingClientId { get; set; }
    public DateTime ConflictDateTimeIn { get; set; }
    public DateTime ConflictDateTimeOut { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.Now;
}
