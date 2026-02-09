using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using JMScheduler.Job.Configuration;
using JMScheduler.Job.Data;
using JMScheduler.Job.Infrastructure;
using JMScheduler.Job.Services;

// ============================================================================
// JMScheduler.Job — Console application replacing MySQL stored procedures:
//   - CallProcessScheduleModal (orchestrator)
//   - ProcessScheduleModal (weekly/multi-week, 650 lines)
//   - ProcessScheduleModal_Monthly (monthly, 257 lines)
//   - SpanClientScheduleShift (multi-week date calculator, 374 lines)
//   - ClientShiftModalEditable (reset model anchors, 35 lines)
//
// Usage:
//   JMScheduler.Job.exe                          (uses current date/time)
//   JMScheduler.Job.exe "2026-02-09 06:00:00"    (specify start datetime)
//
// Scheduling:
//   Use Windows Task Scheduler to run this at the desired frequency.
//   Example: daily at 2:00 AM EST
//
// Configuration:
//   appsettings.json — connection string, batch sizes, advance days, logging
//   Environment variables prefixed with JM_ can override any setting
//   appsettings.{DOTNET_ENVIRONMENT}.json for environment-specific overrides
// ============================================================================

// --- Configuration ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json",
        optional: true)
    .AddEnvironmentVariables(prefix: "JM_")
    .Build();

// --- Serilog ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "JMScheduler")
    .CreateLogger();

try
{
    Log.Information("JMScheduler starting up");

    // --- DI Container ---
    var services = new ServiceCollection();

    // Bind strongly-typed configuration
    var schedulerConfig = new SchedulerConfig();
    configuration.GetSection(SchedulerConfig.SectionName).Bind(schedulerConfig);
    services.AddSingleton(schedulerConfig);

    // Logging
    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        builder.AddSerilog(dispose: true);
    });

    // Infrastructure
    var connectionString = configuration.GetConnectionString("SchedulerDb")
        ?? throw new InvalidOperationException("Missing 'SchedulerDb' connection string in configuration.");

    services.AddSingleton(new DbConnectionFactory(connectionString));
    services.AddSingleton<DeadlockRetryHandler>();

    // Data layer
    services.AddSingleton<ScheduleRepository>();

    // Services
    services.AddSingleton<MultiWeekDateCalculator>();
    services.AddSingleton<CleanupService>();
    services.AddSingleton<WeeklyScheduleService>();
    services.AddSingleton<MonthlyScheduleService>();

    // Orchestrator
    services.AddSingleton<SchedulerJob>();

    var sp = services.BuildServiceProvider();

    // --- Parse optional command-line datetime ---
    var scheduleDateTime = DateTime.Now;
    if (args.Length > 0 && DateTime.TryParse(args[0], out var parsed))
    {
        scheduleDateTime = parsed;
        Log.Information("Using command-line schedule date: {Date:yyyy-MM-dd HH:mm:ss}", scheduleDateTime);
    }
    else
    {
        Log.Information("Using current date/time: {Date:yyyy-MM-dd HH:mm:ss}", scheduleDateTime);
    }

    // --- Run the job ---
    var job = sp.GetRequiredService<SchedulerJob>();
    using var cts = new CancellationTokenSource();

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress += (_, e) =>
    {
        Log.Warning("Cancellation requested via Ctrl+C");
        e.Cancel = true;
        cts.Cancel();
    };

    await job.RunAsync(scheduleDateTime, cts.Token);

    return 0; // success exit code
}
catch (OperationCanceledException)
{
    Log.Warning("Job was cancelled");
    return 1;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception — scheduler job terminated");
    return 2;
}
finally
{
    await Log.CloseAndFlushAsync();
}
