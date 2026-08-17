using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace BuildingBlocks;

/// <summary>Type-activated Postgres health check shared across services, each supplying its own connection-string name.</summary>
public sealed class PostgresHealthCheck(IConfiguration configuration, string connectionStringName) : IHealthCheck
{
    private readonly string _connectionString = configuration.GetConnectionString(connectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is required.");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", exception);
        }
    }
}
