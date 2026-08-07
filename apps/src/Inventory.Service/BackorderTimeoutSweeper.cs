using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

/// <summary>
/// Milestone 74: gives up on a backorder nobody restocked in time.
///
/// Waiting is not free for the customer - an order parked in Backordered
/// forever is a support ticket, not a courtesy. Unlike
/// Payments.Service.PaymentAuthorizationSweeper, there is no money on hold
/// to release here (payment is decided one saga step after reservation, so
/// a backordered order was never charged) - what this sweeper releases is
/// the customer's wait. It reuses InventoryReservationReplied with
/// Backordered: false, which OrderSagaReplyConsumer already treats as a
/// permanent refusal: no new saga-side code needed, this looks like an
/// ordinary "insufficient stock" reply that happened to arrive late.
///
/// Same single-sweeper reasoning as PaymentAuthorizationSweeper: SKIP
/// LOCKED already makes concurrent sweeps safe, so the advisory lock exists
/// only to stop every replica polling every tick, not for correctness.
/// </summary>
public sealed class BackorderTimeoutSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<BackorderOptions> options,
    ILogger<BackorderTimeoutSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Distinct from Payments' SweepLockKey by construction - advisory
    /// locks are per-database, and this is a different database - but kept
    /// numerically distinct too, so a grep for either value is unambiguous
    /// about which service it belongs to.
    /// </summary>
    private const long SweepLockKey = 7400_0001;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly BackorderOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.TimeoutSweepIntervalSeconds);

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
                // A failed sweep must never take the host down - the
                // backorders it did not time out this pass are still
                // claimable next time.
                BackorderSweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var isSweeper = await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({SweepLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!isSweeper)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromMinutes(_options.TimeoutMinutes);

        var claimedIds = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT reservation_id AS "Value"
                FROM backorders
                WHERE requested_at <= {cutoff}
                ORDER BY requested_at
                LIMIT {_options.TimeoutSweepBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var backorders = await dbContext.Backorders
            .Where(backorder => claimedIds.Contains(backorder.ReservationId))
            .ToListAsync(cancellationToken);

        foreach (var backorder in backorders)
        {
            dbContext.Backorders.Remove(backorder);

            var reply = new InventoryReservationReplied(
                backorder.ReservationId,
                backorder.OrderId,
                backorder.Sku,
                backorder.Quantity,
                Reserved: false,
                Reason: "backorder timed out without restock",
                backorder.CorrelationId,
                now,
                Backordered: false);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(InventoryReservationReplied),
                JsonSerializer.Serialize(reply, SerializerOptions),
                now,
                backorder.CorrelationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (backorders.Count > 0)
        {
            BackorderSweeperLog.TimedOut(logger, backorders.Count);
        }
    }
}

public sealed partial class BackorderSweeperLog
{
    [LoggerMessage(EventId = 9020, Level = LogLevel.Error, Message = "Backorder timeout sweep failed; backorders it did not time out remain claimable next pass")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Warning, Message = "Timed out {Count} backorder(s) that outlasted their wait window without a restock")]
    public static partial void TimedOut(ILogger logger, int count);
}
