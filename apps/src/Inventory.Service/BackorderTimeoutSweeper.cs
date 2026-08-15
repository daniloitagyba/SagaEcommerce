using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

/// <summary>
/// Gives up on a backorder nobody restocked in time - an
/// order parked in Backordered forever is a support ticket, not a courtesy.
/// No money is on hold here (payment is decided one step after reservation)
/// - what this releases is the customer's wait. Reuses
/// InventoryReservationReplied with Backordered: false, which
/// OrderSagaReplyConsumer already treats as a permanent refusal, so no new
/// saga-side code is needed. Same single-sweeper reasoning as
/// PaymentAuthorizationSweeper: <see cref="SweepLockKey"/> only stops
/// wasted polling across replicas.
///
/// <see cref="SkuAdvisoryLock"/> is the mechanism that actually matters for
/// correctness here: this sweeper and
/// InventoryReservationMessageProcessor.ReleaseBackordersAsync (a restock
/// arriving for the same SKU) both delete rows from the same
/// <c>backorders</c> table, and without a shared lock between them a
/// restock's read of "which backorders are still pending" and this sweep's
/// delete of one of those exact rows can land in either order with no
/// coordination at all.
/// </summary>
public sealed class BackorderTimeoutSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<BackorderOptions> options,
    TimeProvider timeProvider,
    ILogger<BackorderTimeoutSweeper> logger) : BackgroundService
{
    /// <summary>Kept numerically distinct from Payments' SweepLockKey so a grep for either value is unambiguous about which service it belongs to.</summary>
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
                // A failed sweep must never take the host down - backorders it missed this pass are still claimable next time.
                BackorderSweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    /// <summary>Public so integration tests can drive it directly, the same testable seam SagaTimeoutSweeper's own SweepOnceAsync already gives.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
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

        var now = timeProvider.GetUtcNow();
        var cutoff = now - TimeSpan.FromMinutes(_options.TimeoutMinutes);

        // A candidates pass only, not the authoritative read - each SKU's
        // rows are re-read for real immediately below, after that SKU's own
        // advisory lock is held, so this cannot act on a row a concurrent
        // restock already claimed.
        var candidateSkus = await dbContext.Backorders
            .Where(backorder => backorder.RequestedAt <= cutoff)
            .Select(backorder => backorder.Sku)
            .Distinct()
            .OrderBy(sku => sku)
            .Take(_options.TimeoutSweepBatchSize)
            .ToListAsync(cancellationToken);

        if (candidateSkus.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var timedOut = 0;
        foreach (var sku in candidateSkus)
        {
            // Blocks here until any in-flight reserve/commit/release/restock
            // for this SKU (including ReleaseBackordersAsync's own delete of
            // the same rows) has committed or rolled back - see this
            // class's own comment for why that is what makes the re-read
            // below trustworthy.
            await SkuAdvisoryLock.AcquireAsync(dbContext, sku, cancellationToken);

            var pending = await dbContext.Backorders
                .Where(backorder => backorder.Sku == sku && backorder.RequestedAt <= cutoff)
                .ToListAsync(cancellationToken);

            foreach (var backorder in pending)
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

                timedOut++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (timedOut > 0)
        {
            BackorderSweeperLog.TimedOut(logger, timedOut);
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
