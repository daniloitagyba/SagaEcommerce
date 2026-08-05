using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BuildingBlocks;

public sealed class RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = connectionMultiplexer.GetDatabase();
            await database.PingAsync();

            return HealthCheckResult.Healthy("Redis is reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", exception);
        }
    }
}
