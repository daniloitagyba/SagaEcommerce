namespace Orders.Domain;

/// <summary>
/// Milestone 66: what the customer actually bought. Until now Order was
/// amount-only (Milestone 7) and the saga faked a SKU by hashing the order
/// id (SagaSkuMapper) - so "reserve inventory" reserved a product nobody
/// ordered. Everything downstream that claims to be about a purchase
/// (inventory reservation, bestseller tracking, the orchestrated saga's
/// compensation) was operating on a stand-in until this type existed.
///
/// UnitPrice is snapshotted at checkout from the Catalog's current price,
/// not carried over from whatever the cart cached when the item was added -
/// the cart deliberately shows the price the shopper saw (see
/// CartLineItem), and checkout is where that gets revalidated against
/// reality. A shopper who left an item in the cart for a week pays today's
/// price, and the order records what was actually charged.
///
/// LineDiscount is this line's prorated share of the order-level discounts,
/// distributed with NodaMoney's Split so the shares always sum to exactly
/// the order's DiscountTotal - never a cent more or less. Storing it per
/// line (rather than only at the order level) is what makes partial
/// refunds and per-item margin reporting possible later without
/// re-deriving an allocation that may no longer reproduce.
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

    /// <summary>
    /// Milestone 70: how many of this line's units have come back.
    ///
    /// Tracked per line rather than per return so "may this customer return
    /// two more?" is answerable without replaying every prior return, and
    /// so the refund calculator knows which per-unit shares are still
    /// unclaimed.
    /// </summary>
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
