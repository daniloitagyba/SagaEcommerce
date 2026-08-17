using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Defines the leader election contract.
/// </summary>
public enum LeaderElectionMode
{
    Kubernetes,
    SingleNode
}

public interface ILeaderElection
{
    bool IsLeader { get; }
}

/// <summary>
/// Provides leader election for a single instance.
/// </summary>
public sealed class SingleNodeLeaderElection : ILeaderElection
{
    public bool IsLeader => true;
}

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
/// Provides Kubernetes lease-based leader election.
/// </summary>
public sealed class LeaderElectionService(
    IOptions<LeaderElectionOptions> options,
    IConfiguration configuration,
    ILogger<LeaderElectionService> logger) : BackgroundService, ILeaderElection
{
    private readonly LeaderElectionOptions _options = options.Value;
    private readonly string _identity = configuration["InstanceId"] ?? Environment.MachineName;
    private volatile bool _isLeader;

    public bool IsLeader => _isLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPeriod = TimeSpan.FromSeconds(_options.RetryPeriodSeconds);
        LeaderElectionServiceLog.Starting(logger, _identity, _options.LeaseName);

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
