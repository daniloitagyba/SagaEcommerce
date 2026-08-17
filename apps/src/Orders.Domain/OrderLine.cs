namespace Orders.Domain;

/// <summary>What the customer actually bought; UnitPrice is snapshotted at checkout, not whatever the cart cached.</summary>
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

    /// <summary>This line's prorated share of the order's total tax, weighted by its discounted value.</summary>
    public decimal LineTax { get; private set; }

    /// <summary>How many of this line's units have come back.</summary>
    public int ReturnedQuantity { get; private set; }

    public int ReturnableQuantity => Quantity - ReturnedQuantity;

    internal void RecordReturn(int quantity) => ReturnedQuantity += quantity;

    internal static OrderLine Create(Guid orderId, OrderLineDraft draft)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(draft.Sku) || string.IsNullOrWhiteSpace(draft.ProductName))
        {
            throw new ArgumentException("SKU and product name are required.", nameof(draft));
        }

        if (draft.Quantity <= 0 || draft.UnitPrice < 0m || draft.LineDiscount < 0m || draft.LineTax < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "Quantity must be positive and monetary values cannot be negative.");
        }

        var lineSubtotal = draft.UnitPrice * draft.Quantity;
        if (draft.LineDiscount > lineSubtotal)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "Line discount cannot exceed line subtotal.");
        }

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
            LineTotal = lineSubtotal - draft.LineDiscount,
            LineTax = draft.LineTax
        };
    }
}

/// <summary>The application layer's input for building an order line, priced and discounted before it reaches the aggregate.</summary>
public sealed record OrderLineDraft(
    string Sku,
    string ProductName,
    string CategorySlug,
    int Quantity,
    decimal UnitPrice,
    decimal LineDiscount,
    decimal LineTax = 0m);
