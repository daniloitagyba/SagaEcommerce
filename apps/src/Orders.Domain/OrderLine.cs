namespace Orders.Domain;

/// <summary>
/// Milestone 66: what the customer actually bought - before this, Order was
/// amount-only (Milestone 7) and the saga faked a SKU by hashing the order
/// id, so "reserve inventory" reserved a product nobody ordered.
///
/// UnitPrice is snapshotted at checkout from Catalog's current price, not
/// whatever the cart cached when the item was added - a shopper who left
/// an item for a week pays today's price. LineDiscount is this line's
/// prorated share of the order-level discount, stored per line so partial
/// refunds and per-item margin reporting don't need to re-derive an
/// allocation that may no longer reproduce.
/// </summary>
public sealed class OrderLine
{
    private OrderLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string ProductName { get; private set; } = string.Empty;

    public string CategorySlug { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    /// <summary>UnitPrice * Quantity, before any discount.</summary>
    public decimal LineSubtotal { get; private set; }

    /// <summary>This line's prorated share of the order's total discount.</summary>
    public decimal LineDiscount { get; private set; }

    /// <summary>LineSubtotal - LineDiscount.</summary>
    public decimal LineTotal { get; private set; }

    /// <summary>Milestone 70: how many of this line's units have come back - tracked per line so "may this customer return two more?" needs no replay.</summary>
    public int ReturnedQuantity { get; private set; }

    public int ReturnableQuantity => Quantity - ReturnedQuantity;

    internal void RecordReturn(int quantity) => ReturnedQuantity += quantity;

    internal static OrderLine Create(Guid orderId, OrderLineDraft draft)
    {
        var lineSubtotal = draft.UnitPrice * draft.Quantity;

        return new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Sku = draft.Sku,
            ProductName = draft.ProductName,
            CategorySlug = draft.CategorySlug,
            Quantity = draft.Quantity,
            UnitPrice = draft.UnitPrice,
            LineSubtotal = lineSubtotal,
            LineDiscount = draft.LineDiscount,
            LineTotal = lineSubtotal - draft.LineDiscount
        };
    }
}

/// <summary>
/// The application layer's input for building an order line: a SKU priced
/// against the live catalog and already assigned its share of the order's
/// discounts. Keeps Order.CreateWithLines from having to know anything
/// about how pricing arrived at these numbers.
/// </summary>
public sealed record OrderLineDraft(
    string Sku,
    string ProductName,
    string CategorySlug,
    int Quantity,
    decimal UnitPrice,
    decimal LineDiscount);
