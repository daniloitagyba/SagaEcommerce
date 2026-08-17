namespace Orders.Domain;

/// <summary>Mirrors BuildingBlocks.OrderStatuses' Created and Delivered constants for this dependency-free domain.</summary>
internal static class OrderStatusNames
{
    public const string Created = "Created";
    public const string Delivered = "Delivered";
}

public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>The grand total actually charged: Subtotal - DiscountTotal + ShippingTotal + TaxTotal.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Sum of every line's UnitPrice * Quantity, before discounts.</summary>
    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal ShippingTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    /// <summary>The coupon the shopper presented, if any, kept for audit of DiscountTotal.</summary>
    public string? CouponCode { get; private set; }

    /// <summary>The automatic campaign applied, if any, kept for audit.</summary>
    public string? CampaignCode { get; private set; }

    /// <summary>"Card", "Pix" or "Boleto", deciding whether Payments authorizes now or charges outright.</summary>
    public string PaymentMethod { get; private set; } = DefaultPaymentMethod;

    /// <summary>Delivery destination; null on the amount-only shape, which falls back to flat shipping and global tax.</summary>
    public ShippingAddress? ShippingAddress { get; private set; }

    private const string DefaultPaymentMethod = "Pix";

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Every line fully returned - the point at which the order itself becomes Returned.</summary>
    public bool IsFullyReturned => _lines.Count > 0 && _lines.All(line => line.ReturnableQuantity == 0);

    /// <summary>Builds a return for some of this order's units, validating and computing refunds in one pass.</summary>
    public (OrderReturn? Return, ReturnRejectionReason Rejection, string? OffendingSku) TryReturn(
        IReadOnlyList<(string Sku, int Quantity)> requestedItems,
        string reason,
        ReturnReasonCategory reasonCategory,
        TimeSpan regretWindow,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(requestedItems);

        if (Status != OrderStatusNames.Delivered)
        {
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

            var goodsRefund = ReturnRefundCalculator.RefundForUnits(
                new NodaMoney.Money(line.LineTotal, currency),
                line.Quantity,
                line.ReturnedQuantity,
                quantity,
                currency);

            var taxRefund = ReturnRefundCalculator.RefundForUnits(
                new NodaMoney.Money(line.LineTax, currency),
                line.Quantity,
                line.ReturnedQuantity,
                quantity,
                currency);

            returnLines.Add(OrderReturnLine.Create(sku, quantity, (goodsRefund + taxRefund).Amount));
        }

        for (var index = 0; index < requestedItems.Count; index++)
        {
            var (sku, quantity) = requestedItems[index];
            _lines.First(item => string.Equals(item.Sku, sku, StringComparison.Ordinal)).RecordReturn(quantity);
        }

        var shippingRefundOwed = ShippingRefundPolicy.IsOwed(IsFullyReturned, reasonCategory, CreatedAt, requestedAt, regretWindow);
        var shippingRefund = shippingRefundOwed ? ShippingTotal : 0m;

        var orderReturn = OrderReturn.Create(
            Id, CustomerId, Currency, reason, reasonCategory, shippingRefund, requestedAt, returnLines);
        return (orderReturn, ReturnRejectionReason.None, null);
    }

    /// <summary>The original amount-only constructor; Subtotal is just the amount and everything else is zero.</summary>
    public static Order Create(string customerId, decimal amount, string currency, DateTimeOffset createdAt)
    {
        EnsureIdentityAndMoney(customerId, amount, currency);

        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = OrderStatusNames.Created,
            CreatedAt = createdAt,
            Subtotal = amount,
            DiscountTotal = 0m,
            ShippingTotal = 0m,
            TaxTotal = 0m,
            PaymentMethod = DefaultPaymentMethod
        };
    }

    /// <summary>Builds an order from priced line items; Amount is derived, not accepted, so it always matches its parts.</summary>
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
        ShippingAddress? shippingAddress,
        string? campaignCode = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required.", nameof(currency));
        }

        if (discountTotal < 0m || shippingTotal < 0m || taxTotal < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(discountTotal), "Order totals cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            throw new ArgumentException("Payment method is required.", nameof(paymentMethod));
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order must have at least one line.", nameof(lines));
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Currency = currency,
            Status = OrderStatusNames.Created,
            CreatedAt = createdAt,
            CouponCode = couponCode,
            CampaignCode = campaignCode,
            PaymentMethod = paymentMethod,
            ShippingAddress = shippingAddress
        };

        foreach (var draft in lines)
        {
            order._lines.Add(OrderLine.Create(order.Id, draft));
        }

        order.Subtotal = order._lines.Sum(line => line.LineSubtotal);
        if (discountTotal > order.Subtotal)
        {
            throw new ArgumentOutOfRangeException(nameof(discountTotal), "Discount cannot exceed the subtotal.");
        }
        order.DiscountTotal = discountTotal;
        order.ShippingTotal = shippingTotal;
        order.TaxTotal = taxTotal;
        order.Amount = order.Subtotal - discountTotal + shippingTotal + taxTotal;

        return order;
    }

    private static void EnsureIdentityAndMoney(string customerId, decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required.", nameof(currency));
        }
    }
}
