using Serilog;
using JMScheduler.Api.Middleware;
using JMScheduler.Core.Configuration;
using JMScheduler.Core.Data;
using JMScheduler.Core.Infrastructure;
using JMScheduler.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Serilog ---
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig.ReadFrom.Configuration(context.Configuration)
        .Enrich.WithProperty("Application", "JMScheduler.Api");
});

// --- Scheduler Core DI ---
var schedulerConfig = new SchedulerConfig();
builder.Configuration.GetSection(SchedulerConfig.SectionName).Bind(schedulerConfig);
builder.Services.AddSingleton(schedulerConfig);

var connectionString = builder.Configuration.GetConnectionString("SchedulerDb")
    ?? throw new InvalidOperationException("Missing 'SchedulerDb' connection string in configuration.");

builder.Services.AddSingleton(new DbConnectionFactory(connectionString));
builder.Services.AddSingleton<DeadlockRetryHandler>();
builder.Services.AddSingleton<ScheduleRepository>();
builder.Services.AddSingleton<MultiWeekDateCalculator>();
builder.Services.AddSingleton<CleanupService>();
builder.Services.AddSingleton<WeeklyScheduleService>();
builder.Services.AddSingleton<MonthlyScheduleService>();
builder.Services.AddSingleton<SchedulerJob>();

builder.Services.AddControllers();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();

Log.Information("JMScheduler API starting on {Urls}", string.Join(", ", app.Urls));

app.Run();
