namespace Orders.Domain;

public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>
    /// The grand total actually charged: Subtotal - DiscountTotal +
    /// ShippingTotal + TaxTotal. Named "Amount", not "GrandTotal", since
    /// every consumer since Milestone 7 already carries that name.
    /// </summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Sum of every line's UnitPrice * Quantity, before discounts.</summary>
    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal ShippingTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    /// <summary>The coupon the shopper presented, if any - kept so DiscountTotal stays auditable.</summary>
    public string? CouponCode { get; private set; }

    /// <summary>
    /// Milestone 68: "Card" or "Pix", deciding whether Payments holds an
    /// authorization to capture at shipment or charges outright. A plain
    /// string, not a BuildingBlocks.PaymentMethods reference - Orders.Domain
    /// must not depend on the messaging contracts' wire format.
    /// </summary>
    public string PaymentMethod { get; private set; } = DefaultPaymentMethod;

    /// <summary>
    /// Milestone 71: where it is going. Null on the amount-only shape,
    /// which has no destination and falls back to flat shipping and the
    /// global tax rate.
    /// </summary>
    public ShippingAddress? ShippingAddress { get; private set; }

    private const string DefaultPaymentMethod = "Pix";

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Every line fully returned - the point at which the order itself becomes Returned.</summary>
    public bool IsFullyReturned => _lines.Count > 0 && _lines.All(line => line.ReturnableQuantity == 0);

    /// <summary>
    /// Milestone 70: builds a return for some of this order's units.
    /// Validation and refund arithmetic share one pass over the same fact -
    /// how much of each line is still returnable - so it's read only once.
    /// </summary>
    public (OrderReturn? Return, ReturnRejectionReason Rejection, string? OffendingSku) TryReturn(
        IReadOnlyList<(string Sku, int Quantity)> requestedItems,
        string reason,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(requestedItems);

        if (Status != "Delivered")
        {
            // Nothing can come back that never arrived.
            return (null, ReturnRejectionReason.OrderNotDelivered, null);
        }

        if (requestedItems.Count == 0)
        {
            return (null, ReturnRejectionReason.NothingToReturn, null);
        }

        var currency = NodaMoney.Currency.FromCode(Currency);
        var returnLines = new List<OrderReturnLine>(requestedItems.Count);

        foreach (var (sku, quantity) in requestedItems)
        {
            if (quantity <= 0)
            {
                return (null, ReturnRejectionReason.QuantityNotPositive, sku);
            }

            var line = _lines.FirstOrDefault(item => string.Equals(item.Sku, sku, StringComparison.Ordinal));
            if (line is null)
            {
                return (null, ReturnRejectionReason.UnknownSku, sku);
            }

            if (quantity > line.ReturnableQuantity)
            {
                return (null, ReturnRejectionReason.ExceedsPurchasedQuantity, sku);
            }

            var refund = ReturnRefundCalculator.RefundForUnits(
                new NodaMoney.Money(line.LineTotal, currency),
                line.Quantity,
                line.ReturnedQuantity,
                quantity,
                currency);

            returnLines.Add(OrderReturnLine.Create(sku, quantity, refund.Amount));
        }

        // Mutate only after every line is validated - no half-returned order.
        for (var index = 0; index < requestedItems.Count; index++)
        {
            var (sku, quantity) = requestedItems[index];
            _lines.First(item => string.Equals(item.Sku, sku, StringComparison.Ordinal)).RecordReturn(quantity);
        }

        var orderReturn = OrderReturn.Create(Id, CustomerId, Currency, reason, requestedAt, returnLines);
        return (orderReturn, ReturnRejectionReason.None, null);
    }

    /// <summary>
    /// The Milestone 7 amount-only constructor, kept so k6, Pact and the
    /// README's quickstart - all still POSTing {customerId, amount,
    /// currency} - don't break. Subtotal is just the amount; everything
    /// else is zero.
    /// </summary>
    public static Order Create(string customerId, decimal amount, string currency, DateTimeOffset createdAt)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = "Created",
            CreatedAt = createdAt,
            Subtotal = amount,
            DiscountTotal = 0m,
            ShippingTotal = 0m,
            TaxTotal = 0m,
            PaymentMethod = DefaultPaymentMethod
        };
    }

    /// <summary>
    /// The real checkout path: an order built from priced line items, with
    /// the totals the pricing engine computed. Amount is derived here rather
    /// than accepted as a parameter, so no caller can persist a grand total
    /// that disagrees with the parts it is made of.
    /// </summary>
    public static Order CreateWithLines(
        string customerId,
        string currency,
        DateTimeOffset createdAt,
        string? couponCode,
        IReadOnlyList<OrderLineDraft> lines,
        decimal discountTotal,
        decimal shippingTotal,
        decimal taxTotal,
        string paymentMethod,
        ShippingAddress? shippingAddress)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order must have at least one line.", nameof(lines));
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Currency = currency,
            Status = "Created",
            CreatedAt = createdAt,
            CouponCode = couponCode,
            PaymentMethod = paymentMethod,
            ShippingAddress = shippingAddress
        };

        foreach (var draft in lines)
        {
            order._lines.Add(OrderLine.Create(order.Id, draft));
        }

        order.Subtotal = order._lines.Sum(line => line.LineSubtotal);
        order.DiscountTotal = discountTotal;
        order.ShippingTotal = shippingTotal;
        order.TaxTotal = taxTotal;
        order.Amount = order.Subtotal - discountTotal + shippingTotal + taxTotal;

        return order;
    }
}
