namespace JMScheduler.Job.Models;

/// <summary>
/// Represents a row from job_ClientscheduleShiftnextrunStatus.
/// This table tracks multi-week recurring models (recurringon > 1) to determine
/// when they should next generate shifts.
///
/// Table columns (inferred from INSERT in schedular.sql line 43):
///   modal_id          INT  — FK to clientschedulemodel.Id
///   employeeid        INT  — from model
///   Client_id         INT  — from model
///   Nextscheduledate  DATETIME — initially set to model's startdate; updated after each run
///   Changestatus      INT  — 0=no change, 1=changed during this run
///   ModalEditmode     INT  — 0=normal, >0=model was edited, use NOW() as restriction date
/// </summary>
public sealed class NextRunStatus
{
    /// <summary>FK to clientschedulemodel.Id.</summary>
    public int ModalId { get; set; }

    public int EmployeeId { get; set; }
    public int Client_id { get; set; }

    /// <summary>
    /// The anchor date from which multi-week schedule dates are calculated.
    /// Initially the model's startdate; updated to the last generated schedule date after each run.
    /// </summary>
    public DateTime Nextscheduledate { get; set; }

    /// <summary>0 = no change this run, 1 = shifts were created during this run.</summary>
    public int Changestatus { get; set; }

    /// <summary>
    /// 0 = normal mode (use last scheduled shift date as restriction).
    /// >0 = model was edited/reset (use NOW() as restriction, regenerate future shifts).
    /// Set by ClientShiftModalEditable logic.
    /// </summary>
    public int ModalEditmode { get; set; }
}
