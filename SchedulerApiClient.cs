// =============================================================================
// SchedulerApiClient.cs
// =============================================================================
// Drop this file into the portal project (e.g. /Services/SchedulerApiClient.cs)
//
// PURPOSE:
//   Provides a typed HTTP client for calling the JMScheduler API from within
//   the portal. The most common use case is triggering a single-model run
//   whenever a schedule model is created or edited, so only that model's
//   shifts are (re)generated rather than running the full job.
//
// SETUP (3 steps):
//   1. Add settings to portal's appsettings.json  (see Step 1 below)
//   2. Register the client in Program.cs / Startup.cs  (see Step 2 below)
//   3. Inject SchedulerApiClient wherever you need it  (see Step 3 below)
//
// REQUIREMENTS:
//   - .NET Core / .NET 5+ (uses IHttpClientFactory — built into ASP.NET Core)
//   - No NuGet packages required beyond what ASP.NET Core already provides
// =============================================================================

// -----------------------------------------------------------------------
// STEP 1 — appsettings.json
// -----------------------------------------------------------------------
// Add the following block to the portal's appsettings.json.
// Keep the ApiKey out of source control (use appsettings.Production.json
// or environment variables on the server).
//
//  "SchedulerApi": {
//    "BaseUrl": "https://portal.janitorialmanager.com/JMSchedular",
//    "ApiKey":  "QUJKwiDpuBfwlynFA/hkDd9RHC2b1zWO846Jakzcy4Y="
//  }
//
// -----------------------------------------------------------------------

// -----------------------------------------------------------------------
// STEP 2 — Register in Program.cs (ASP.NET Core) or Startup.cs (Framework)
// -----------------------------------------------------------------------
// In Program.cs add BEFORE builder.Build():
//
//   builder.Services.AddHttpClient<SchedulerApiClient>((sp, client) =>
//   {
//       var config = sp.GetRequiredService<IConfiguration>();
//       client.BaseAddress = new Uri(config["SchedulerApi:BaseUrl"]!.TrimEnd('/') + "/");
//       client.DefaultRequestHeaders.Add("X-Api-Key", config["SchedulerApi:ApiKey"]);
//       client.Timeout = TimeSpan.FromMinutes(10); // full runs can take a few minutes
//   });
//
// For classic Startup.cs (ConfigureServices method):
//
//   services.AddHttpClient<SchedulerApiClient>((sp, client) =>
//   {
//       var config = sp.GetRequiredService<IConfiguration>();
//       client.BaseAddress = new Uri(config["SchedulerApi:BaseUrl"]!.TrimEnd('/') + "/");
//       client.DefaultRequestHeaders.Add("X-Api-Key", config["SchedulerApi:ApiKey"]);
//       client.Timeout = TimeSpan.FromMinutes(10);
//   });
//
// -----------------------------------------------------------------------

// -----------------------------------------------------------------------
// STEP 3 — Inject and call from any Controller or Service
// -----------------------------------------------------------------------
// Example: trigger shift regeneration when a model is created or edited
//
//   public class ClientScheduleModelController : Controller
//   {
//       private readonly SchedulerApiClient _scheduler;
//
//       public ClientScheduleModelController(SchedulerApiClient scheduler)
//       {
//           _scheduler = scheduler;
//       }
//
//       [HttpPost]
//       public async Task<IActionResult> Create(ClientScheduleModel model)
//       {
//           // ... insert model to DB first ...
//
//           // NEW model — just generate shifts (nothing to delete)
//           var result = await _scheduler.RunForModelAsync(model.Id, advanceDays: 365);
//
//           if (result == null || !result.IsSuccess)
//               _logger.LogWarning("Scheduler did not complete for model {ModelId}", model.Id);
//
//           return RedirectToAction("Index");
//       }
//
//       [HttpPost]
//       public async Task<IActionResult> Edit(ClientScheduleModel model)
//       {
//           // ... update model in DB first ...
//
//           // EDITED model — delete old future shifts, regenerate with new settings
//           var result = await _scheduler.ResetAndRegenerateModelAsync(model.Id, advanceDays: 365);
//
//           if (result != null && result.IsSuccess)
//               _logger.LogInformation(
//                   "Model {ModelId} reset: deleted {Deleted}, created {Created}",
//                   model.Id, result.ResetShiftsDeleted, result.ShiftsCreated);
//
//           return RedirectToAction("Index");
//       }
//   }
//
// -----------------------------------------------------------------------

using System.Net.Http.Json;

/// <summary>
/// Typed HTTP client for the JMScheduler API.
/// Inject via constructor — registered as a typed HttpClient (IHttpClientFactory).
/// </summary>
public sealed class SchedulerApiClient
{
    private readonly HttpClient _http;

    // Injected automatically by IHttpClientFactory — do not construct manually.
    public SchedulerApiClient(HttpClient http)
    {
        _http = http;
    }

    // -------------------------------------------------------------------------
    // PRIMARY USE CASE: Run for a single model
    // -------------------------------------------------------------------------
    // Call this after a schedule model is created or edited so that only
    // that model's shifts are regenerated. Pass a generous advanceDays value
    // (e.g. 365) so the full future window is covered for the model.
    //
    // Example:
    //   var result = await _scheduler.RunForModelAsync(modelId: 4512, advanceDays: 365);
    // -------------------------------------------------------------------------

    /// <summary>
    /// Trigger a scheduler run for a single model only.
    /// Use this when a NEW schedule model is created (no existing shifts to delete).
    /// </summary>
    /// <param name="modelId">The clientschedulemodel.Id to process.</param>
    /// <param name="advanceDays">
    /// How many days ahead to generate shifts. Use 365 for a full year.
    /// Defaults to 45 (same as the daily cron run).
    /// </param>
    public async Task<SchedulerRunResult?> RunForModelAsync(
        int modelId,
        int advanceDays = 45,
        CancellationToken ct = default)
    {
        return await RunAsync(modelId: modelId, advanceDays: advanceDays, ct: ct);
    }

    /// <summary>
    /// Delete all future unlinked shifts for a model and regenerate them.
    /// Use this when a schedule model is EDITED (day/time/frequency changed).
    ///
    /// What it does:
    ///   1. Deletes all shifts for this model that are tomorrow or later
    ///      AND have no timecard linked (today and past are never touched).
    ///   2. Regenerates shifts with the model's current settings.
    ///
    /// This is the method to call after saving model changes in the portal.
    /// </summary>
    /// <param name="modelId">The clientschedulemodel.Id to reset and regenerate.</param>
    /// <param name="advanceDays">
    /// How many days ahead to generate. Use 365 for a full year.
    /// </param>
    public async Task<SchedulerRunResult?> ResetAndRegenerateModelAsync(
        int modelId,
        int advanceDays = 365,
        CancellationToken ct = default)
    {
        return await RunAsync(modelId: modelId, advanceDays: advanceDays, reset: true, ct: ct);
    }

    // -------------------------------------------------------------------------
    // SECONDARY USE CASES
    // -------------------------------------------------------------------------

    /// <summary>
    /// Run for all models belonging to a single company.
    /// Useful from a company admin page ("regenerate all schedules for this company").
    /// </summary>
    public async Task<SchedulerRunResult?> RunForCompanyAsync(
        int companyId,
        int advanceDays = 90,
        CancellationToken ct = default)
    {
        return await RunAsync(companyId: companyId, advanceDays: advanceDays, ct: ct);
    }

    /// <summary>
    /// Run the full daily job (all companies, all models).
    /// Equivalent to the crontab call. Not normally needed from the portal
    /// but useful for an admin "Run Now" button.
    /// </summary>
    public async Task<SchedulerRunResult?> RunAllAsync(
        int advanceDays = 45,
        int monthlyMonthsAhead = 3,
        CancellationToken ct = default)
    {
        return await RunAsync(
            advanceDays: advanceDays,
            monthlyMonthsAhead: monthlyMonthsAhead,
            ct: ct);
    }

    /// <summary>
    /// Check if the scheduler API is reachable and healthy.
    /// Does not require authentication — safe to call from a dashboard health check.
    /// Returns true if the API responds with HTTP 200.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/scheduler/status", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // INTERNAL: Core method all public methods delegate to
    // -------------------------------------------------------------------------

    /// <summary>
    /// Low-level run method. All public methods call this.
    /// Returns null if the request fails or a non-success HTTP status is returned.
    /// Logs should be checked on the server side via job_scheduler_run table.
    /// </summary>
    private async Task<SchedulerRunResult?> RunAsync(
        int companyId = 0,
        int modelId = 0,
        int advanceDays = 0,
        int monthlyMonthsAhead = 0,
        bool reset = false,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("api/scheduler/run", new
            {
                companyId,
                modelId,
                advanceDays,
                monthlyMonthsAhead,
                reset
            }, ct);

            // 409 = another run already in progress — not an error, just busy
            if ((int)response.StatusCode == 409)
            {
                return new SchedulerRunResult
                {
                    Status = "Blocked",
                    ErrorMessage = "Another scheduler run is already in progress. Try again shortly."
                };
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SchedulerRunResult>(cancellationToken: ct);
        }
        catch (TaskCanceledException)
        {
            // Request timed out (Timeout set to 10 min in registration — should not happen)
            return new SchedulerRunResult
            {
                Status = "Timeout",
                ErrorMessage = "Scheduler request timed out. The job may still be running."
            };
        }
        catch (HttpRequestException ex)
        {
            // Network / connection error
            return new SchedulerRunResult
            {
                Status = "Failed",
                ErrorMessage = $"Could not reach scheduler API: {ex.Message}"
            };
        }
    }
}

// =============================================================================
// Response model — mirrors the JSON returned by POST /api/scheduler/run
// =============================================================================

/// <summary>
/// Result returned by the JMScheduler API after a run completes.
/// All counts are 0 when the job was blocked or failed before processing.
/// </summary>
public sealed class SchedulerRunResult
{
    /// <summary>Unique ID for this run. Use to look up detail in job_scheduler_run table.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// "Completed" — job finished normally.
    /// "Blocked"   — another run was already in progress (try again later).
    /// "Cancelled" — job was cancelled mid-run.
    /// "Failed"    — job threw an unhandled exception (check ErrorMessage + server logs).
    /// "Timeout"   — HTTP request from portal timed out (job may still be running on server).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Number of new shifts inserted into clientscheduleshift.</summary>
    public int ShiftsCreated { get; set; }

    /// <summary>Number of shifts skipped because they already existed (expected on re-runs).</summary>
    public int DuplicatesSkipped { get; set; }

    /// <summary>Number of shifts blocked due to employee overlap at a different location.</summary>
    public int OverlapsBlocked { get; set; }

    /// <summary>Number of orphaned future shifts deleted during cleanup phase.</summary>
    public int OrphanedDeleted { get; set; }

    /// <summary>Number of reset/inactive model shifts deleted during cleanup phase.</summary>
    public int ResetDeleted { get; set; }

    /// <summary>Number of future shifts deleted by reset=true before regeneration.</summary>
    public int ResetShiftsDeleted { get; set; }

    /// <summary>Number of weekly models that were loaded and processed.</summary>
    public int WeeklyModelsLoaded { get; set; }

    /// <summary>Total elapsed seconds for the run.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Non-null only when Status is "Failed" or "Blocked". Human-readable error.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>True if the job completed without errors.</summary>
    public bool IsSuccess => Status == "Completed";
}
