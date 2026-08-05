using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public sealed class LeaderElectionOptions
{
    public const string SectionName = "LeaderElection";

    public string Namespace { get; init; } = "orders-lab";

    public string LeaseName { get; init; } = "orders-worker-saga-sweeper";

    public int LeaseDurationSeconds { get; init; } = 15;

    public int RenewDeadlineSeconds { get; init; } = 10;

    public int RetryPeriodSeconds { get; init; } = 2;
}

/// <summary>
/// Milestone 36: Kubernetes Lease-based leader election (k8s.LeaderElection,
/// the C# port of client-go's leaderelection package) so that exactly one
/// orders-worker replica actively runs SagaTimeoutSweeper at a time, rather
/// than every KEDA-scaled replica redundantly polling. Requires
/// automountServiceAccountToken: true on this pod (previously false, a
/// Milestone 26 hardening default) - InClusterConfig() needs the projected
/// ServiceAccount token to authenticate to the API server. Scoped tightly:
/// RBAC grants this ServiceAccount access to nothing but Lease objects in
/// this one namespace.
/// </summary>
public sealed class LeaderElectionService(
    IOptions<LeaderElectionOptions> options,
    IConfiguration configuration,
    ILogger<LeaderElectionService> logger) : BackgroundService
{
    private readonly LeaderElectionOptions _options = options.Value;
    private readonly string _identity = configuration["InstanceId"] ?? Environment.MachineName;
    private volatile bool _isLeader;

    public bool IsLeader => _isLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPeriod = TimeSpan.FromSeconds(_options.RetryPeriodSeconds);
        LeaderElectionServiceLog.Starting(logger, _identity, _options.LeaseName);

        // RunUntilLeadershipLostAsync completes once leadership is lost and,
        // unlike client-go's Go original, does not retry acquisition on its
        // own - this outer loop keeps re-attempting for the pod's lifetime.
        // Building the Kubernetes client (InClusterConfig, inside the try
        // below) can fail exactly the same way leadership itself can - it
        // throws outright when there's no ServiceAccount token to load,
        // which is normal and expected outside a real cluster - so it gets
        // the same catch-log-retry treatment instead of running unguarded,
        // where the exception would go unhandled and (with this host's
        // BackgroundServiceExceptionBehavior=StopHost) take the whole
        // process down over what is, for this service, a routine and
        // permanently-recoverable-on-the-next-retry condition.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var kubernetesConfig = KubernetesClientConfiguration.InClusterConfig();
                using var client = new Kubernetes(kubernetesConfig);
                var resourceLock = new LeaseLock(client, _options.Namespace, _options.LeaseName, _identity);
                var electionConfig = new LeaderElectionConfig(resourceLock)
                {
                    LeaseDuration = TimeSpan.FromSeconds(_options.LeaseDurationSeconds),
                    RenewDeadline = TimeSpan.FromSeconds(_options.RenewDeadlineSeconds),
                    RetryPeriod = retryPeriod
                };

                using var elector = new LeaderElector(electionConfig);
                elector.OnStartedLeading += () =>
                {
                    _isLeader = true;
                    LeaderElectionServiceLog.StartedLeading(logger, _identity);
                };
                elector.OnStoppedLeading += () =>
                {
                    _isLeader = false;
                    LeaderElectionServiceLog.StoppedLeading(logger, _identity);
                };
                elector.OnNewLeader += leaderIdentity => LeaderElectionServiceLog.NewLeaderObserved(logger, leaderIdentity, _identity);
                elector.OnError += exception => LeaderElectionServiceLog.ElectionError(logger, _identity, exception);

                await elector.RunUntilLeadershipLostAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LeaderElectionServiceLog.ElectionError(logger, _identity, exception);
                _isLeader = false;
                await Task.Delay(retryPeriod, stoppingToken);
            }
        }

        _isLeader = false;
    }
}

public sealed partial class LeaderElectionServiceLog
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Leader election starting for identity {Identity} on lease {LeaseName}")]
    public static partial void Starting(ILogger logger, string identity, string leaseName);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Instance {Identity} became the leader")]
    public static partial void StartedLeading(ILogger logger, string identity);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Instance {Identity} stopped being the leader")]
    public static partial void StoppedLeading(ILogger logger, string identity);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Debug, Message = "Observed leader {LeaderIdentity} (self is {Identity})")]
    public static partial void NewLeaderObserved(ILogger logger, string leaderIdentity, string identity);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Error, Message = "Leader election error on instance {Identity}")]
    public static partial void ElectionError(ILogger logger, string identity, Exception exception);
}
