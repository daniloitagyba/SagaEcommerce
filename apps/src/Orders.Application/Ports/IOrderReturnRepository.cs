using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderReturnRepository
{
    /// <summary>Loads an order with its lines for return validation, tracked so RecordReturn persists.</summary>
    Task<Order?> FindForReturnAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the return, the updated per-line returned quantities, the
    /// refund and restock commands, and - when everything has come back -
    /// the order's move to Returned. All in one transaction: a refund
    /// command that outlived a rolled-back return would give money away for
    /// goods the shopper still has.
    /// </summary>
    Task SaveReturnAsync(
        Order order,
        OrderReturn orderReturn,
        bool markOrderReturned,
        string correlationId,
        CancellationToken cancellationToken);
}
