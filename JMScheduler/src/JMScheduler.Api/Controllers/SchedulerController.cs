using Microsoft.AspNetCore.Mvc;
using JMScheduler.Api.Models;
using JMScheduler.Core.Services;

namespace JMScheduler.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SchedulerController : ControllerBase
{
    private readonly SchedulerJob _schedulerJob;
    private readonly ILogger<SchedulerController> _logger;

    private static volatile bool _isRunning;
    private static readonly object _lock = new();

    public SchedulerController(SchedulerJob schedulerJob, ILogger<SchedulerController> logger)
    {
        _schedulerJob = schedulerJob;
        _logger = logger;
    }

    /// <summary>
    /// Trigger a scheduler run with optional filters.
    /// Returns 409 Conflict if another run is already in progress (in-process guard).
    /// The DB-level StartEvent concurrency guard is also still active.
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(SchedulerRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Run([FromBody] SchedulerRunRequest? request, CancellationToken ct)
    {
        request ??= new SchedulerRunRequest();

        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Scheduler run rejected — another run is already in progress");
                return Conflict(new { error = "A scheduler run is already in progress. Try again later." });
            }
            _isRunning = true;
        }

        try
        {
            _logger.LogInformation(
                "API scheduler run starting: CompanyId={CompanyId}, ModelId={ModelId}, " +
                "AdvanceDays={AdvanceDays}, MonthlyMonthsAhead={MonthlyMonthsAhead}",
                request.CompanyId, request.ModelId, request.AdvanceDays, request.MonthlyMonthsAhead);

            var result = await _schedulerJob.RunAsync(
                DateTime.Now,
                companyId: request.CompanyId,
                modelId: request.ModelId,
                advanceDaysOverride: request.AdvanceDays,
                monthlyMonthsAheadOverride: request.MonthlyMonthsAhead,
                ct: ct);

            var response = new SchedulerRunResponse
            {
                RunId = result.RunId,
                Status = result.Status,
                ShiftsCreated = result.ShiftsCreated,
                DuplicatesSkipped = result.DuplicatesSkipped,
                OverlapsBlocked = result.OverlapsBlocked,
                OrphanedDeleted = result.OrphanedDeleted,
                ResetDeleted = result.ResetDeleted,
                WeeklyModelsLoaded = result.WeeklyModelsLoaded,
                AuditEntries = result.AuditEntries,
                Conflicts = result.Conflicts,
                DurationSeconds = result.DurationSeconds,
                ErrorMessage = result.ErrorMessage
            };

            if (result.Status == "Blocked")
            {
                return Conflict(response);
            }

            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request was cancelled by the client." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler run failed with exception");
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            lock (_lock)
            {
                _isRunning = false;
            }
        }
    }

    /// <summary>
    /// Health/status check — no authentication required.
    /// Returns 200 if the API is running and whether a job is currently in progress.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        return Ok(new
        {
            status = "healthy",
            isRunning = _isRunning,
            timestamp = DateTime.Now,
            version = typeof(SchedulerController).Assembly.GetName().Version?.ToString() ?? "1.0.0"
        });
    }
}
