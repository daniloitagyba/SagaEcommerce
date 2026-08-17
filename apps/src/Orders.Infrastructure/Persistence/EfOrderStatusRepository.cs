using BuildingBlocks;
using Orders.Application.Exceptions;
using Orders.Application.Ports;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderStatusRepository(OrderTransitionExecutor executor) : IOrderStatusRepository
{
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
            return await executor.TryTransitionAsync(
                orderId,
                targetStatus,
                allowedFrom,
                correlationId,
                includeCancellationCompensation: true,
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
