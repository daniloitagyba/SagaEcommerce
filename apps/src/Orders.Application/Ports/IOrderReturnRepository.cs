using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderReturnRepository
{
    /// <summary>Loads an order with its lines for return validation, tracked so RecordReturn persists.</summary>
    Task<Order?> FindForReturnAsync(Guid orderId, CancellationToken cancellationToken);

    Task SaveReturnAsync(
        Order order,
        OrderReturn orderReturn,
        bool markOrderReturned,
        string correlationId,
        CancellationToken cancellationToken);
}
