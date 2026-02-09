using MySqlConnector;

namespace JMScheduler.Job.Infrastructure;

/// <summary>
/// Creates MySQL connections with consistent session settings
/// (timezone, tmp_table_size) matching the original stored procedure.
/// </summary>
public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Opens a new connection with session-level settings applied.
    /// Caller is responsible for disposing.
    /// </summary>
    public async Task<MySqlConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Match the session settings from the original stored procedure
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SET SESSION time_zone = 'US/Eastern';
            SET SESSION tmp_table_size = 536870912;
            SET SESSION max_heap_table_size = 536870912;";
        await cmd.ExecuteNonQueryAsync(ct);

        return conn;
    }
}
