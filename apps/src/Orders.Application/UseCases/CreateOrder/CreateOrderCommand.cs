namespace Orders.Application.UseCases.CreateOrder;

/// <summary>
/// One requested line: a SKU and how many. Deliberately <em>no</em> price -
/// the client tells us what it wants to buy, never what it costs. The
/// unit price is read from the catalog server-side at checkout, so a
/// tampered request cannot buy a television for one centavo.
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
    /// <summary>Milestone 68: Card or Pix. Null means Pix - see PaymentMethods.</summary>
    string? PaymentMethod = null,
    /// <summary>Milestone 71: destination. Null falls back to flat shipping and the global tax rate.</summary>
    Orders.Domain.ShippingAddress? ShippingAddress = null)
{
    /// <summary>
    /// Milestone 66 runs both request shapes side by side - the expand half
    /// of an expand/contract migration - so a pricing bug stays
    /// distinguishable from a migration bug while both are live.
    /// </summary>
    public bool IsLineItemCheckout => Items is { Count: > 0 };
}
