using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed partial class EfOrderStatusRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderStatusRepository
{
    private const string TransitionSql = """
        WITH previous AS (
            SELECT status, payment_method, coupon_code, customer_id, amount_cents FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.payment_method, previous.coupon_code, previous.customer_id, previous.amount_cents
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                var row = await TryTransitionRowAsync(orderId, targetStatus, allowedFrom, ct);
                var previousStatus = row.PreviousStatus;
                var paymentMethod = row.PaymentMethod;

                if (previousStatus is null)
                {
                    var exists = await dbContext.Orders.AnyAsync(item => item.Id == orderId, ct);
                    await transaction.RollbackAsync(ct);
                    return new OrderTransition(
                        exists ? OrderTransitionOutcome.NotApplicable : OrderTransitionOutcome.NotFound,
                        null);
                }

                var now = DateTimeOffset.UtcNow;
                var version = await NextEventVersionAsync(ct);
                dbContext.OutboxMessages.Add(OutboxMessage.Create(
                    Guid.NewGuid(),
                    nameof(OrderStatusChanged),
                    JsonSerializer.Serialize(
                        new OrderStatusChanged(Guid.NewGuid(), orderId, targetStatus, now, correlationId, version),
                        SerializerOptions),
                    now,
                    correlationId,
                    System.Diagnostics.Activity.Current?.Id,
                    System.Diagnostics.Activity.Current?.TraceStateString));

                if (targetStatus == OrderStatuses.Cancelled)
                {
                    await ReleaseCouponAsync(orderId, ct);
                    await ReleaseCampaignAsync(orderId, ct);
                }

                if (targetStatus == OrderStatuses.Confirmed)
                {
                    if (row.CouponCode is not null)
                    {
                        await ConfirmCouponAsync(orderId, ct);
                    }

                    await ConfirmCampaignAsync(orderId, ct);

                    if (row.CustomerId is not null)
                    {
                        await RecordCompletedOrderForTierAsync(row.CustomerId, row.Amount, ct);
                    }
                }

                if (targetStatus == OrderStatuses.Cancelled
                    && row.CustomerId is not null
                    && previousStatus is OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold)
                {
                    await ReverseCompletedOrderForTierAsync(row.CustomerId, row.Amount, ct);
                }

                if (settlementAction == OrderSettlementAction.Capture && paymentMethod is not null && PaymentMethods.RequiresCapture(paymentMethod))
                {
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                }
                else if (settlementAction == OrderSettlementAction.Cancel)
                {
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                    await QueueInventoryCompensationAsync(orderId, previousStatus, correlationId, ct);
                    await FlagInFlightSagaAsCancelledAsync(orderId, previousStatus, ct);
                }

                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new OrderTransition(OrderTransitionOutcome.Advanced, paymentMethod);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    /// <summary>Atomic compare-and-swap status transition via raw ADO.NET, since ExecuteSqlInterpolatedAsync cannot return the RETURNING columns.</summary>
    private async Task<TransitionRow> TryTransitionRowAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var dbTransaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = TransitionSql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, targetStatus);
        command.Parameters.AddWithValue("allowed_from", NpgsqlDbType.Array | NpgsqlDbType.Varchar, allowedFrom.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return TransitionRow.NotTransitioned;
        }

        return new TransitionRow(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            reader.GetInt64(4) / 100m);
    }

    /// <summary>Allocates the next value from the cross-process monotonic order_event_version_seq counter used for projection ordering.</summary>
    private async Task<long> NextEventVersionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var dbTransaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = "SELECT nextval('order_event_version_seq')";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed record TransitionRow(
        string? PreviousStatus,
        string? PaymentMethod,
        string? CouponCode,
        string? CustomerId,
        decimal Amount)
    {
        public static readonly TransitionRow NotTransitioned = new(null, null, null, null, 0m);
    }

    /// <summary>Queues the inventory compensation appropriate to the order's actual previous status (restock, release queue slot, or nothing pending saga reply) rather than guessing.</summary>
    private async Task QueueInventoryCompensationAsync(
        Guid orderId,
        string previousStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (previousStatus == OrderStatuses.Backordered)
        {
            var request = new BackorderCancellationRequested(orderId, correlationId, now);
            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(BackorderCancellationRequested),
                JsonSerializer.Serialize(request, SerializerOptions),
                now,
                correlationId,
                System.Diagnostics.Activity.Current?.Id,
                System.Diagnostics.Activity.Current?.TraceStateString));
            return;
        }

        if (previousStatus is not (OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold))
        {
            return;
        }

        var lines = await dbContext.OrderLines
            .Where(line => line.OrderId == orderId)
            .Select(line => new { line.Sku, line.Quantity })
            .ToListAsync(cancellationToken);

        foreach (var line in lines)
        {
            var request = new InventoryRestockRequested(Guid.NewGuid(), orderId, line.Sku, line.Quantity, correlationId, now);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(InventoryRestockRequested),
                JsonSerializer.Serialize(request, SerializerOptions),
                now,
                correlationId,
                System.Diagnostics.Activity.Current?.Id,
                System.Diagnostics.Activity.Current?.TraceStateString));
        }
    }

    /// <summary>Idempotently flags an in-flight saga row as cancelled so OrderSagaReplyConsumer releases or restocks instead of confirming, closing the race a Created/Backordered cancellation has with an in-progress saga.</summary>
    private async Task FlagInFlightSagaAsCancelledAsync(Guid orderId, string previousStatus, CancellationToken cancellationToken)
    {
        if (previousStatus is not (OrderStatuses.Created or OrderStatuses.Backordered))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE saga_orchestration_states
            SET cancellation_requested_at = COALESCE(cancellation_requested_at, {DateTimeOffset.UtcNow})
            WHERE order_id = {orderId}
            """,
            cancellationToken);
    }

    /// <summary>Releases a coupon redemption slot, guarded to fire exactly once no matter how many times a cancellation is retried.</summary>
    private async Task ReleaseCouponAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH released AS (
                UPDATE coupon_redemptions
                SET state = {CouponRedemptionState.Released}, settled_at = {DateTimeOffset.UtcNow}
                WHERE order_id = {orderId}
                  AND state IN ({CouponRedemptionState.Reserved}, {CouponRedemptionState.Confirmed})
                RETURNING code
            )
            UPDATE coupons
            SET redemption_count = GREATEST(redemption_count - 1, 0)
            FROM released
            WHERE coupons.code = released.code
            """,
            cancellationToken);
    }

    /// <summary>Confirms a coupon redemption, guarded on Reserved so a redelivered confirm is a no-op rather than a double count.</summary>
    private async Task ConfirmCouponAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE coupon_redemptions
            SET state = {CouponRedemptionState.Confirmed}, settled_at = {DateTimeOffset.UtcNow}
            WHERE order_id = {orderId} AND state = {CouponRedemptionState.Reserved}
            """,
            cancellationToken);
    }

    /// <summary>Records a completed order toward customer tier, incrementing spend and re-deriving tier in one atomic UPDATE to avoid a lost-update race between concurrent confirmation paths.</summary>
    private async Task RecordCompletedOrderForTierAsync(string customerId, decimal amount, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE customers
            SET lifetime_spend = lifetime_spend + {amount},
                completed_order_count = completed_order_count + 1,
                tier = CASE
                    WHEN lifetime_spend + {amount} >= {CustomerTiers.GoldThreshold} THEN {CustomerTiers.Gold}
                    WHEN lifetime_spend + {amount} >= {CustomerTiers.SilverThreshold} THEN {CustomerTiers.Silver}
                    ELSE {CustomerTiers.Bronze}
                END
            WHERE id = {customerId}
            """,
            cancellationToken);
    }

    /// <summary>Reverses a completed order's tier contribution, deliberately not re-deriving tier downward, floored at zero like ReleaseSql's counterpart.</summary>
    private async Task ReverseCompletedOrderForTierAsync(string customerId, decimal amount, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE customers
            SET lifetime_spend = GREATEST(lifetime_spend - {amount}, 0),
                completed_order_count = GREATEST(completed_order_count - 1, 0)
            WHERE id = {customerId}
            """,
            cancellationToken);
    }

    private void QueueSettlementCommand(
        Guid orderId,
        OrderSettlementAction action,
        string targetStatus,
        string correlationId)
    {
        var now = DateTimeOffset.UtcNow;

        var (eventType, payload) = action == OrderSettlementAction.Capture
            ? (nameof(PaymentCaptureRequested),
               JsonSerializer.Serialize(new PaymentCaptureRequested(orderId, correlationId, now), SerializerOptions))
            : (nameof(PaymentCancellationRequested),
               JsonSerializer.Serialize(new PaymentCancellationRequested(orderId, $"order {targetStatus.ToLowerInvariant()}", correlationId, now), SerializerOptions));

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            eventType,
            payload,
            now,
            correlationId,
            System.Diagnostics.Activity.Current?.Id,
            System.Diagnostics.Activity.Current?.TraceStateString));
    }
}
