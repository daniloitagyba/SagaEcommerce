using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Polly;
using Polly.Registry;

namespace Payments.Service;

/// <summary>Releases card authorizations nobody ever captured, so a lost hold doesn't encumber the shopper's funds indefinitely.</summary>
public sealed class PaymentAuthorizationSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSettlementOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentAuthorizationSweeper> logger,
    ResiliencePipelineProvider<string> pipelineProvider) : BackgroundService
{
    /// <summary>Arbitrary but fixed; the only advisory lock this database uses.</summary>
    private const long SweepLockKey = 6800_0001;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentSettlementOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.ExpirySweepIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                PaymentSweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var isSweeper = await dbContext.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({SweepLockKey}) AS \"Value\"")
                .SingleAsync(ct);

            if (!isSweeper)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var now = timeProvider.GetUtcNow();

            var claimedIds = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT id AS "Value"
                    FROM payments
                    WHERE is_primary
                      AND state IN ({PaymentStates.Authorized}, {PaymentStates.AwaitingPayment})
                      AND authorization_expires_at IS NOT NULL
                      AND authorization_expires_at <= {now}
                    ORDER BY authorization_expires_at
                    LIMIT {_options.ExpirySweepBatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            if (claimedIds.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var payments = await dbContext.Payments
                .Where(payment => claimedIds.Contains(payment.Id))
                .ToListAsync(ct);

            var expired = 0;
            foreach (var payment in payments)
            {
                if (!payment.TrySettleWithoutCapture(PaymentStates.Expired, "payment window elapsed without settlement", now))
                {
                    continue;
                }

                expired++;

                var reply = new PaymentSettlementReplied(
                    payment.OrderId,
                    payment.Id,
                    payment.State,
                    payment.Amount,
                    payment.Currency,
                    payment.CorrelationId,
                    now,
                    RequiresReconciliation: true);

                dbContext.OutboxMessages.Add(OutboxMessage.Create(
                    Guid.NewGuid(),
                    nameof(PaymentSettlementReplied),
                    JsonSerializer.Serialize(reply, SerializerOptions),
                    now,
                    payment.CorrelationId,
                    Activity.Current?.Id,
                    Activity.Current?.TraceStateString));
            }

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            if (expired > 0)
            {
                PaymentSettlementLog.ExpiredAuthorizations(logger, expired);
            }
        }, cancellationToken);
    }
}

public sealed partial class PaymentSweeperLog
{
    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Authorization expiry sweep failed; holds it did not release remain claimable next pass")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}
