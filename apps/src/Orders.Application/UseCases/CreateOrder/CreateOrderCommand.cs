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
    /// Milestone 66 runs both request shapes side by side rather than
    /// cutting over at once - the expand half of an expand/contract
    /// migration. The line-item shape is the real one; the amount-only
    /// shape is what the k6 scripts, smoke tests, Pact contracts and the
    /// README quickstart have posted since Milestone 7, and keeping them
    /// working is what makes it possible to tell a pricing bug apart from
    /// a migration bug while both exist.
    /// </summary>
    public bool IsLineItemCheckout => Items is { Count: > 0 };
}
