using NodaMoney;
using Orders.Domain.Pricing;

namespace Orders.Domain;

/// <summary>A customer sending some or all of an order back; partial returns are the ordinary case.</summary>
public sealed class OrderReturn
{
    private readonly List<OrderReturnLine> _lines = [];

    private OrderReturn()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    /// <summary>What refund policy this return is under, separate from the free-text <see cref="Reason"/>.</summary>
    public ReturnReasonCategory ReasonCategory { get; private set; }

    /// <summary>This order's outbound shipping, refunded only on a complete return under a policy that owes it.</summary>
    public decimal ShippingRefund { get; private set; }

    /// <summary>What the customer gets back: every returned line's goods and tax, plus ShippingRefund when owed.</summary>
    public decimal RefundTotal { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public IReadOnlyList<OrderReturnLine> Lines => _lines;

    internal static OrderReturn Create(
        Guid orderId,
        string customerId,
        string currency,
        string reason,
        ReturnReasonCategory reasonCategory,
        decimal shippingRefund,
        DateTimeOffset requestedAt,
        IReadOnlyList<OrderReturnLine> lines)
    {
        var orderReturn = new OrderReturn
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            Currency = currency,
            Reason = reason,
            ReasonCategory = reasonCategory,
            ShippingRefund = shippingRefund,
            RequestedAt = requestedAt
        };

        foreach (var line in lines)
        {
            line.AttachTo(orderReturn.Id);
            orderReturn._lines.Add(line);
        }

        orderReturn.RefundTotal = orderReturn._lines.Sum(line => line.RefundAmount) + shippingRefund;
        return orderReturn;
    }
}

/// <summary>The policy that decides whether outbound shipping comes back on a complete return.</summary>
public enum ReturnReasonCategory
{
    /// <summary>The item was faulty or wrong. Shipping refunds in full on a complete return, no window.</summary>
    Defect,

    /// <summary>The shopper changed their mind. Shipping refunds in full on a complete return, but only inside the regret window.</summary>
    Regret,

    /// <summary>A discretionary return outside any legal entitlement. Goods and tax come back; shipping does not.</summary>
    Unwanted
}

/// <summary>Determines whether outbound shipping is owed on a return.</summary>
public static class ShippingRefundPolicy
{
    public static bool IsOwed(
        bool orderFullyReturned,
        ReturnReasonCategory reasonCategory,
        DateTimeOffset orderCreatedAt,
        DateTimeOffset requestedAt,
        TimeSpan regretWindow)
    {
        if (!orderFullyReturned)
        {
            return false;
        }

        return reasonCategory switch
        {
            ReturnReasonCategory.Defect => true,
            ReturnReasonCategory.Regret => requestedAt - orderCreatedAt <= regretWindow,
            _ => false
        };
    }
}

public sealed class OrderReturnLine
{
    private OrderReturnLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReturnId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    /// <summary>This line's share of what was actually charged, net of the discount it received.</summary>
    public decimal RefundAmount { get; private set; }

    internal static OrderReturnLine Create(string sku, int quantity, decimal refundAmount) =>
        new() { Id = Guid.NewGuid(), Sku = sku, Quantity = quantity, RefundAmount = refundAmount };

    internal void AttachTo(Guid returnId) => ReturnId = returnId;
}

public enum ReturnRejectionReason
{
    None,
    OrderNotDelivered,
    UnknownSku,
    QuantityNotPositive,
    ExceedsPurchasedQuantity,
    NothingToReturn
}

/// <summary>Works out what a partial return is worth, based on the line's discounted total, not list price.</summary>
public static class ReturnRefundCalculator
{
    /// <summary>Prices a partial return as a slice of the line's charged total, consuming a disjoint cumulative range.</summary>
    public static Money RefundForUnits(
        Money lineTotal,
        int lineQuantity,
        int alreadyReturned,
        int returningNow,
        Currency currency)
    {
        if (returningNow <= 0 || lineQuantity <= 0)
        {
            return new Money(0m, currency);
        }

        var upToNow = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned + returningNow, currency);
        var upToBefore = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned, currency);

        return upToNow - upToBefore;
    }
}
