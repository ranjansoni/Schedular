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
    private static volatile SchedulerRunResponse? _lastResult;

    public SchedulerController(SchedulerJob schedulerJob, ILogger<SchedulerController> logger)
    {
        _schedulerJob = schedulerJob;
        _logger = logger;
    }

    /// <summary>
    /// Trigger a scheduler run. Returns 202 Accepted immediately — the job
    /// runs in the background. Poll GET /api/scheduler/status to see the result.
    ///
    /// Routing logic:
    ///   - ModelId > 0  → lean single-model path (no lock, concurrent-safe)
    ///   - ModelId == 0 → full batch path (in-process lock + DB concurrency guard)
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Run([FromBody] SchedulerRunRequest? request)
    {
        request ??= new SchedulerRunRequest();

        if (request.Reset && request.ModelId <= 0)
        {
            return BadRequest(new { error = "Reset requires a ModelId. Pass modelId > 0 when using reset." });
        }

        if (request.ModelId > 0)
        {
            return RunSingleModel(request);
        }

        return RunBatch(request);
    }

    private IActionResult RunSingleModel(SchedulerRunRequest request)
    {
        var runId = $"single-{request.ModelId}-{DateTime.Now:yyyyMMddHHmmss}";

        _logger.LogInformation(
            "API single-model run queued: RunId={RunId}, ModelId={ModelId}, Reset={Reset}, " +
            "AdvanceDays={AdvanceDays}, MonthlyMonths={Monthly}",
            runId, request.ModelId, request.Reset, request.AdvanceDays, request.MonthlyMonthsAhead);

        var job = _schedulerJob;
        var logger = _logger;
        var req = request;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await job.RunSingleModelAsync(
                    req.ModelId, req.Reset, req.AdvanceDays, req.MonthlyMonthsAhead,
                    CancellationToken.None);

                _lastResult = MapResponse(result);

                logger.LogInformation(
                    "Single-model run completed: RunId={RunId}, Status={Status}, Created={Created}",
                    result.RunId, result.Status, result.ShiftsCreated);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background single-model run failed for ModelId={ModelId}", req.ModelId);
                _lastResult = new SchedulerRunResponse
                {
                    RunId = runId,
                    Status = "Failed",
                    ErrorMessage = ex.Message
                };
            }
        });

        return Accepted(new { runId, message = "Job started. Poll GET /api/scheduler/status for result." });
    }

    private IActionResult RunBatch(SchedulerRunRequest request)
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

        var runId = Guid.NewGuid().ToString();

        _logger.LogInformation(
            "API batch run queued: RunId={RunId}, CompanyId={CompanyId}, AdvanceDays={AdvanceDays}, " +
            "MonthlyMonthsAhead={MonthlyMonthsAhead}",
            runId, request.CompanyId, request.AdvanceDays, request.MonthlyMonthsAhead);

        var job = _schedulerJob;
        var logger = _logger;
        var req = request;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await job.RunAsync(
                    DateTime.Now,
                    companyId: req.CompanyId,
                    advanceDaysOverride: req.AdvanceDays,
                    monthlyMonthsAheadOverride: req.MonthlyMonthsAhead,
                    ct: CancellationToken.None);

                _lastResult = MapResponse(result);

                logger.LogInformation(
                    "Batch run completed: RunId={RunId}, Status={Status}, Created={Created}",
                    result.RunId, result.Status, result.ShiftsCreated);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background batch run failed");
                _lastResult = new SchedulerRunResponse
                {
                    RunId = runId,
                    Status = "Failed",
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                lock (_lock)
                {
                    _isRunning = false;
                }
            }
        });

        return Accepted(new { runId, message = "Batch job started. Poll GET /api/scheduler/status for result." });
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
    /// Returns 200 with current state and the last completed run result.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        return Ok(new
        {
            status = "healthy",
            isRunning = _isRunning,
            lastResult = _lastResult,
            timestamp = DateTime.Now,
            version = typeof(SchedulerController).Assembly.GetName().Version?.ToString() ?? "1.0.0"
        });
    }
}
