namespace JMScheduler.Api.Models;

/// <summary>
/// Request body for POST /api/scheduler/run.
/// All fields are optional — defaults are used from appsettings.json when omitted or 0.
/// </summary>
public sealed class SchedulerRunRequest
{
    /// <summary>Company ID to process. 0 or omitted = all companies.</summary>
    public int CompanyId { get; set; }

    /// <summary>Model ID to process. 0 or omitted = all models.</summary>
    public int ModelId { get; set; }

    /// <summary>Number of advance days for weekly shifts. 0 or omitted = use config default.</summary>
    public int AdvanceDays { get; set; }

    /// <summary>Number of months ahead for monthly shifts. 0 or omitted = use config default.</summary>
    public int MonthlyMonthsAhead { get; set; }
}
