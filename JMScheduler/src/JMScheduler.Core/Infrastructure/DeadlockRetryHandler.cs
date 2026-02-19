using MySqlConnector;
using Microsoft.Extensions.Logging;
using JMScheduler.Core.Configuration;

namespace JMScheduler.Core.Infrastructure;

/// <summary>
/// Wraps database operations with automatic retry for transient MySQL errors.
///
/// Handled errors:
///   - 1213: Deadlock found when trying to get lock
///   - 1205: Lock wait timeout exceeded
///   - 1040: Too many connections
///   - 1159/1161: Aborted connection / Got timeout reading communication packets
///   - 2006: MySQL server has gone away
///   - 2013: Lost connection to MySQL server during query
///
/// Retry strategy: exponential backoff with jitter.
/// </summary>
public sealed class DeadlockRetryHandler
{
    // Deadlock / lock contention errors (always retryable)
    private const int MySqlDeadlockErrorCode = 1213;
    private const int MySqlLockWaitTimeoutErrorCode = 1205;

    // Connection / transient errors (retryable with fresh connection)
    private static readonly HashSet<int> TransientErrorCodes = new()
    {
        1213, // Deadlock
        1205, // Lock wait timeout
        1040, // Too many connections
        1159, // Got timeout reading communication packets
        1161, // Aborted connection
        2006, // MySQL server has gone away
        2013, // Lost connection to MySQL server during query
    };

    private readonly SchedulerConfig _config;
    private readonly ILogger<DeadlockRetryHandler> _logger;
    private readonly Random _jitter = new();

    public DeadlockRetryHandler(SchedulerConfig config, ILogger<DeadlockRetryHandler> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Execute an async database action with transient error retry logic.
    /// </summary>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        string operationName,
        CancellationToken ct = default)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _config.MaxDeadlockRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (MySqlException ex) when (IsRetryable(ex) && attempt < _config.MaxDeadlockRetries)
            {
                lastException = ex;
                int baseDelay = _config.DeadlockRetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                // Add jitter: ±25% to avoid thundering herd
                int jitter = _jitter.Next(-baseDelay / 4, baseDelay / 4 + 1);
                int delayMs = Math.Max(50, baseDelay + jitter);

                var errorType = ex.Number == MySqlDeadlockErrorCode ? "Deadlock"
                    : ex.Number == MySqlLockWaitTimeoutErrorCode ? "LockWaitTimeout"
                    : $"TransientError({ex.Number})";

                _logger.LogWarning(
                    "{ErrorType} on {Operation} (attempt {Attempt}/{Max}). " +
                    "Retrying in {Delay}ms. MySQL error: {ErrorCode}, Message: {Message}",
                    errorType, operationName, attempt, _config.MaxDeadlockRetries,
                    delayMs, ex.Number, ex.Message);

                await Task.Delay(delayMs, ct);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("not open", StringComparison.OrdinalIgnoreCase)
                && attempt < _config.MaxDeadlockRetries)
            {
                // Connection was closed/disposed unexpectedly — retry with fresh connection
                lastException = ex;
                int delayMs = _config.DeadlockRetryBaseDelayMs * attempt;

                _logger.LogWarning(
                    "Connection lost on {Operation} (attempt {Attempt}/{Max}). " +
                    "Retrying in {Delay}ms. Error: {Message}",
                    operationName, attempt, _config.MaxDeadlockRetries, delayMs, ex.Message);

                await Task.Delay(delayMs, ct);
            }
        }

        // Final attempt — let exceptions propagate with full context
        try
        {
            return await action();
        }
        catch (Exception ex) when (lastException != null)
        {
            _logger.LogError(ex,
                "Operation {Operation} failed after {MaxRetries} retries. " +
                "Last error: {Message}. First error: {FirstMessage}",
                operationName, _config.MaxDeadlockRetries, ex.Message, lastException.Message);
            throw;
        }
    }

    /// <summary>
    /// Execute an async database action (no return value) with transient error retry logic.
    /// </summary>
    public async Task ExecuteWithRetryAsync(
        Func<Task> action,
        string operationName,
        CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await action();
            return 0; // dummy return
        }, operationName, ct);
    }

    /// <summary>
    /// Check if a MySQL exception is retryable (transient error).
    /// </summary>
    private static bool IsRetryable(MySqlException ex)
    {
        return TransientErrorCodes.Contains(ex.Number)
            || ex.InnerException is System.IO.IOException
            || ex.InnerException is TimeoutException;
    }
}
