using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks;

/// <summary>Caches Kafka broker metadata on a background timer so health probes never block on a Kafka round trip.</summary>
public sealed class KafkaHealthCheck : IHealthCheck, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(3);

    private readonly IAdminClient _adminClient;
    private readonly Timer _timer;
    private readonly Lock _lastResultLock = new();
    private HealthCheckResult _lastResult =
        HealthCheckResult.Unhealthy("Kafka has not been probed yet.");

    public KafkaHealthCheck(IAdminClient adminClient)
    {
        _adminClient = adminClient;
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, RefreshInterval);
    }

    private void Refresh()
    {
        HealthCheckResult result;
        try
        {
            var metadata = _adminClient.GetMetadata(MetadataTimeout);
            result = metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy("Kafka is reachable.")
                : HealthCheckResult.Unhealthy("Kafka returned no broker metadata.");
        }
        catch (Exception exception)
        {
            result = HealthCheckResult.Unhealthy("Kafka is unreachable.", exception);
        }

        lock (_lastResultLock)
        {
            _lastResult = result;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        lock (_lastResultLock)
        {
            return Task.FromResult(_lastResult);
        }
    }

    public void Dispose() => _timer.Dispose();
}
