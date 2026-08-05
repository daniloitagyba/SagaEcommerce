using BuildingBlocks;
using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderRepository
{
    Task AddAsync(Order order, OutboxMessage outboxMessage, CancellationToken cancellationToken);

    Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
