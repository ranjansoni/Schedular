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
    ///
    /// Routing logic:
    ///   - ModelId > 0  → lean single-model path (no lock, no audit, concurrent-safe)
    ///   - ModelId == 0 → full batch path (in-process lock + DB concurrency guard)
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(SchedulerRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Run([FromBody] SchedulerRunRequest? request, CancellationToken ct)
    {
        request ??= new SchedulerRunRequest();

        if (request.Reset && request.ModelId <= 0)
        {
            return BadRequest(new { error = "Reset requires a ModelId. Pass modelId > 0 when using reset." });
        }

        // ---- Single-model lean path: no lock, no batch overhead ----
        if (request.ModelId > 0)
        {
            return await RunSingleModel(request, ct);
        }

        // ---- Full batch path: in-process lock + DB concurrency ----
        return await RunBatch(request, ct);
    }

    private async Task<IActionResult> RunSingleModel(SchedulerRunRequest request, CancellationToken ct)
    {
        _logger.LogInformation(
            "API single-model run: ModelId={ModelId}, Reset={Reset}, AdvanceDays={AdvanceDays}, MonthlyMonths={Monthly}",
            request.ModelId, request.Reset, request.AdvanceDays, request.MonthlyMonthsAhead);

        try
        {
            var result = await _schedulerJob.RunSingleModelAsync(
                request.ModelId,
                request.Reset,
                request.AdvanceDays,
                request.MonthlyMonthsAhead,
                ct);

            var response = MapResponse(result);

            if (result.Status == "NotFound")
                return NotFound(response);

            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request was cancelled by the client." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Single-model run failed for ModelId={ModelId}", request.ModelId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private async Task<IActionResult> RunBatch(SchedulerRunRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Scheduler batch run rejected — another run is already in progress");
                return Conflict(new { error = "A scheduler run is already in progress. Try again later." });
            }
            _isRunning = true;
        }

        try
        {
            _logger.LogInformation(
                "API batch run starting: CompanyId={CompanyId}, AdvanceDays={AdvanceDays}, " +
                "MonthlyMonthsAhead={MonthlyMonthsAhead}",
                request.CompanyId, request.AdvanceDays, request.MonthlyMonthsAhead);

            var result = await _schedulerJob.RunAsync(
                DateTime.Now,
                companyId: request.CompanyId,
                advanceDaysOverride: request.AdvanceDays,
                monthlyMonthsAheadOverride: request.MonthlyMonthsAhead,
                ct: ct);

            var response = MapResponse(result);

            if (result.Status == "Blocked")
                return Conflict(response);

            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Request was cancelled by the client." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler batch run failed");
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

    private static SchedulerRunResponse MapResponse(SchedulerJob.RunResult result) => new()
    {
        RunId = result.RunId,
        Status = result.Status,
        ShiftsCreated = result.ShiftsCreated,
        DuplicatesSkipped = result.DuplicatesSkipped,
        OverlapsBlocked = result.OverlapsBlocked,
        OrphanedDeleted = result.OrphanedDeleted,
        ResetDeleted = result.ResetDeleted,
        ResetShiftsDeleted = result.ResetShiftsDeleted,
        WeeklyModelsLoaded = result.WeeklyModelsLoaded,
        AuditEntries = result.AuditEntries,
        Conflicts = result.Conflicts,
        DurationSeconds = result.DurationSeconds,
        ErrorMessage = result.ErrorMessage
    };

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
