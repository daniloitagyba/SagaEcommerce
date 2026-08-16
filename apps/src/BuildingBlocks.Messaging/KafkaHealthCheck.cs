using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks;

/// <summary>
/// Confluent.Kafka's IAdminClient.GetMetadata has no async overload - it is
/// a blocking librdkafka call by construction. Before Milestone 91 this
/// check wrapped that call in Task.Run on every single probe, which still
/// blocks a thread-pool thread for up to 3 seconds, just someone else's
/// thread instead of the request thread - one per pod per probe interval,
/// indefinitely. This class instead runs that blocking call on its own
/// background timer at a fixed cadence and has every probe simply read the
/// last cached result, so a probe itself never blocks on Kafka at all.
///
/// Must be registered as a singleton (see each Program.cs's
/// `AddSingleton&lt;KafkaHealthCheck&gt;()` ahead of `.AddCheck&lt;KafkaHealthCheck&gt;(...)`)
/// - ASP.NET Core health checks registered via AddCheck&lt;T&gt; default to a
/// transient lifetime, which would construct (and start a new timer for) a
/// fresh instance on every probe, defeating the whole point of caching.
/// </summary>
public sealed class KafkaHealthCheck : IHealthCheck, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(3);

    private readonly IAdminClient _adminClient;
    private readonly Timer _timer;
    // HealthCheckResult is a struct, so it can't be a volatile field
    // (CS0677) - this lock is the same "make the write visible to every
    // reading thread" guarantee volatile would have given a reference
    // type, just expressed the way it has to be for a value type. No
    // contention concern: writes happen once per RefreshInterval from the
    // timer thread, reads happen once per health probe.
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
