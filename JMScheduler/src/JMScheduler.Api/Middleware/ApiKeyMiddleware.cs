namespace JMScheduler.Api.Middleware;

/// <summary>
/// Validates the X-Api-Key header against the configured API key.
/// Allows /api/scheduler/status through without authentication for health checks.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _apiKey = configuration["ApiKey"]
            ?? throw new InvalidOperationException("'ApiKey' is not configured in appsettings.json.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedKey)
            || !string.Equals(extractedKey, _apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }
}
