namespace JMScheduler.Api.Models;

/// <summary>
/// Response body returned from POST /api/scheduler/run.
/// </summary>
public sealed class SchedulerRunResponse
{
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ShiftsCreated { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int OverlapsBlocked { get; set; }
    public int OrphanedDeleted { get; set; }
    public int ResetDeleted { get; set; }
    public int WeeklyModelsLoaded { get; set; }
    public int AuditEntries { get; set; }
    public int Conflicts { get; set; }
    public int DurationSeconds { get; set; }
    public string? ErrorMessage { get; set; }
}
