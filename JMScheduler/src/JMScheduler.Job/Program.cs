using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using JMScheduler.Core.Configuration;
using JMScheduler.Core.Data;
using JMScheduler.Core.Infrastructure;
using JMScheduler.Core.Services;

// ============================================================================
// JMScheduler.Job — Console application replacing MySQL stored procedures.
//
// Usage:
//   JMScheduler.Job.exe                          (uses current date/time)
//   JMScheduler.Job.exe "2026-02-09 06:00:00"    (specify start datetime)
//
// Scheduling:
//   Use Windows Task Scheduler to run this at the desired frequency.
//
// Configuration:
//   appsettings.json — connection string, batch sizes, advance days, logging
//   Environment variables prefixed with JM_ can override any setting
// ============================================================================

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json",
        optional: true)
    .AddEnvironmentVariables(prefix: "JM_")
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "JMScheduler")
    .CreateLogger();

try
{
    Log.Information("JMScheduler starting up");

    var services = new ServiceCollection();

    var schedulerConfig = new SchedulerConfig();
    configuration.GetSection(SchedulerConfig.SectionName).Bind(schedulerConfig);
    services.AddSingleton(schedulerConfig);

    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        builder.AddSerilog(dispose: true);
    });

    var connectionString = configuration.GetConnectionString("SchedulerDb")
        ?? throw new InvalidOperationException("Missing 'SchedulerDb' connection string in configuration.");

    services.AddSingleton(new DbConnectionFactory(connectionString));
    services.AddSingleton<DeadlockRetryHandler>();
    services.AddSingleton<ScheduleRepository>();
    services.AddSingleton<MultiWeekDateCalculator>();
    services.AddSingleton<CleanupService>();
    services.AddSingleton<WeeklyScheduleService>();
    services.AddSingleton<MonthlyScheduleService>();
    services.AddSingleton<SchedulerJob>();

    var sp = services.BuildServiceProvider();

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

    var job = sp.GetRequiredService<SchedulerJob>();
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        Log.Warning("Cancellation requested via Ctrl+C");
        e.Cancel = true;
        cts.Cancel();
    };

    await job.RunAsync(scheduleDateTime, ct: cts.Token);

    return 0;
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
