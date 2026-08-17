using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderReturnRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderReturnRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<Order?> FindForReturnAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async ct => await dbContext.Orders
                    .Include(order => order.Lines)
                    .SingleOrDefaultAsync(order => order.Id == orderId, ct),
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    public async Task SaveReturnAsync(
        Order order,
        OrderReturn orderReturn,
        bool markOrderReturned,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                dbContext.OrderReturns.Add(orderReturn);

                if (markOrderReturned)
                {
                    var rowsAffected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE orders SET status = {OrderStatuses.Returned}
                        WHERE id = {order.Id} AND status = {OrderStatuses.Delivered}
                        """,
                        ct);

                    if (rowsAffected > 0)
                    {
                        await QueueStatusChangedEventAsync(order.Id, correlationId, orderReturn.RequestedAt, ct);

                        await ReverseCompletedOrderForTierAsync(order.CustomerId, order.Amount, ct);
                    }
                }

                QueueRefundCommand(orderReturn, correlationId);
                QueueRestockCommands(orderReturn, correlationId);

                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new OrderReturnConflictException(
                "This order's lines changed since they were read - retry the return.", exception);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    /// <summary>Reverses a customer's tier contribution for a full return, mirroring EfOrderStatusRepository's cancellation-path counterpart; deliberately does not re-derive tier downward.</summary>
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

    private async Task QueueStatusChangedEventAsync(
        Guid orderId, string correlationId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var version = await NextEventVersionAsync(cancellationToken);
        var statusChanged = new OrderStatusChanged(Guid.NewGuid(), orderId, OrderStatuses.Returned, occurredAt, correlationId, version);

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            statusChanged.EventId,
            nameof(OrderStatusChanged),
            JsonSerializer.Serialize(statusChanged, SerializerOptions),
            occurredAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString));
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

    private void QueueRefundCommand(OrderReturn orderReturn, string correlationId)
    {
        var request = new PaymentRefundRequested(
            orderReturn.OrderId,
            orderReturn.Id,
            orderReturn.RefundTotal,
            orderReturn.Currency,
            orderReturn.Reason,
            correlationId,
            orderReturn.RequestedAt);

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            nameof(PaymentRefundRequested),
            JsonSerializer.Serialize(request, SerializerOptions),
            orderReturn.RequestedAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString));
    }

    private void QueueRestockCommands(OrderReturn orderReturn, string correlationId)
    {
        foreach (var line in orderReturn.Lines)
        {
            var request = new InventoryRestockRequested(
                Guid.NewGuid(),
                orderReturn.OrderId,
                line.Sku,
                line.Quantity,
                correlationId,
                orderReturn.RequestedAt);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(InventoryRestockRequested),
                JsonSerializer.Serialize(request, SerializerOptions),
                orderReturn.RequestedAt,
                correlationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString));
        }
    }
}
