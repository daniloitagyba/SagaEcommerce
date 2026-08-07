using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;

namespace Payments.Service;

/// <summary>
/// Milestone 68: releases card authorizations nobody ever captured, so a
/// hold from a lost capture command or an unfulfilled order doesn't
/// encumber the shopper's funds indefinitely - what real acquirers do too.
///
/// <para>
/// Uses a Postgres advisory lock rather than a Kubernetes Lease like
/// Orders.Worker's SagaTimeoutSweeper: <c>FOR UPDATE SKIP LOCKED</c> already
/// makes concurrent sweeps safe (each replica claims a disjoint batch), so
/// the lock only needs to stop wasted polling, not guarantee correctness. A
/// Lease would mean new RBAC and a service account token this pod
/// deliberately doesn't carry (Milestone 26); the advisory lock needs no
/// new infrastructure and releases itself if a replica dies mid-sweep.
/// </para>
/// </summary>
public sealed class PaymentAuthorizationSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSettlementOptions> options,
    ILogger<PaymentAuthorizationSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Arbitrary but fixed - advisory locks share one namespace per
    /// database, so this number must not collide with another one. It is
    /// the only advisory lock this database uses.
    /// </summary>
    private const long SweepLockKey = 6800_0001;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentSettlementOptions _options = options.Value;

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
                // A failed sweep must never take the host down - the holds
                // it did not release this time are still there next time.
                PaymentSweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // pg_try_advisory_xact_lock never waits: a replica that does not get
        // the lock skips this tick entirely instead of queueing behind the
        // one that did. Transaction-scoped, so it is released on commit or
        // rollback - including the rollback in the catch above - and there
        // is no unlock call that can be missed.
        var isSweeper = await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({SweepLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!isSweeper)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // SKIP LOCKED is what makes running this on every replica safe:
        // each one claims a disjoint batch instead of contending for the
        // same rows, and none of them waits on another's lock.
        var claimedIds = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM payments
                WHERE state IN ({PaymentStates.Authorized}, {PaymentStates.AwaitingPayment})
                  AND authorization_expires_at IS NOT NULL
                  AND authorization_expires_at <= {now}
                ORDER BY authorization_expires_at
                LIMIT {_options.ExpirySweepBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var payments = await dbContext.Payments
            .Where(payment => claimedIds.Contains(payment.Id))
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var payment in payments)
        {
            // Goes through the domain guard rather than a bulk UPDATE, so a
            // payment captured between the claim and here is left alone.
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
                now);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(PaymentSettlementReplied),
                JsonSerializer.Serialize(reply, SerializerOptions),
                now,
                payment.CorrelationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (expired > 0)
        {
            PaymentSettlementLog.ExpiredAuthorizations(logger, expired);
        }
    }
}

public sealed partial class PaymentSweeperLog
{
    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Authorization expiry sweep failed; holds it did not release remain claimable next pass")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}
