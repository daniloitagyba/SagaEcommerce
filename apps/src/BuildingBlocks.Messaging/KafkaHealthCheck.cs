using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks;

public sealed class KafkaHealthCheck(IAdminClient adminClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await Task.Run(
                () => adminClient.GetMetadata(TimeSpan.FromSeconds(3)),
                cancellationToken);

            return metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy("Kafka is reachable.")
                : HealthCheckResult.Unhealthy("Kafka returned no broker metadata.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Kafka is unreachable.", exception);
        }
    }
}
