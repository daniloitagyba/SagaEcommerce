namespace Orders.Application.UseCases.CreateOrder;

/// <summary>
/// Represents an order line request.
/// </summary>
public sealed record CreateOrderItem(string? Sku, int Quantity);

public sealed record CreateOrderCommand(
    string? CustomerId,
    decimal Amount,
    string? Currency,
    string CorrelationId,
    string InstanceId,
    string? IdempotencyKey = null,
    IReadOnlyList<CreateOrderItem>? Items = null,
    string? CouponCode = null,
    /// <summary>Card or Pix; null means Pix.</summary>
    string? PaymentMethod = null,
    /// <summary>Destination. Null falls back to flat shipping and the global tax rate.</summary>
    Orders.Domain.ShippingAddress? ShippingAddress = null,
    /// <summary>
    /// Gets the expected order subtotal.
    /// </summary>
    decimal? ExpectedSubtotal = null)
{
    /// <summary>
    /// Both request shapes run side by side as the expand half of an expand/contract migration.
    /// </summary>
    public bool IsLineItemCheckout => Items is { Count: > 0 };
}
