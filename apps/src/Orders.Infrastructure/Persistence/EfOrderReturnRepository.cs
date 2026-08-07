using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
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
            // Tracked, unlike every other read in this repository: the
            // aggregate's RecordReturn mutates the lines, and those changes
            // have to be persisted by SaveReturnAsync.
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
                    // Guarded on Delivered for the same reason every other
                    // status change is: two returns landing at once must not
                    // both believe they were the one that completed the order.
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE orders SET status = {OrderStatuses.Returned}
                        WHERE id = {order.Id} AND status = {OrderStatuses.Delivered}
                        """,
                        ct);
                }

                QueueRefundCommand(orderReturn, correlationId);
                QueueRestockCommands(orderReturn, correlationId);

                // One SaveChanges for the return, the mutated line
                // quantities and the outbox rows together - a refund command
                // that outlived a rolled-back return would give money away
                // for goods the shopper still has.
                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
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
        // One command per SKU rather than one per return: Inventory
        // serialises by SKU partition key (Milestone 41), so a single
        // multi-SKU command would have no correct key to be produced under.
        foreach (var line in orderReturn.Lines)
        {
            var request = new InventoryRestockRequested(
                orderReturn.Id,
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
