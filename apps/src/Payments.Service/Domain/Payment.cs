using BuildingBlocks;

namespace Payments.Service.Domain;

public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Lets the risk decision weigh this customer's history, not just the amount.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// The risk decision, kept alongside <see cref="State"/> rather than
    /// derived from it - "did we agree to take this money?" and "where is
    /// it now?" are different questions.
    /// </summary>
    public bool Approved { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Milestone 68: Card or Pix - see PaymentMethods for why this changes the flow.</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>
    /// Where this order shipped, so the next order can be compared against
    /// it - stored here rather than looked up from Orders, since Payments
    /// must score a decision without a synchronous cross-service call.
    /// </summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public string State { get; private set; } = string.Empty;

    /// <summary>
    /// When a card hold lapses if nobody captures it. Null for anything
    /// that was never authorized - a declined payment or an instant Pix.
    /// </summary>
    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string? SettlementReason { get; private set; }

    /// <summary>
    /// How much of a captured payment has been given back. Cumulative, not
    /// a boolean - returns are partial by nature, so a payment can be
    /// refunded several times before it's fully refunded.
    /// </summary>
    public decimal RefundedAmount { get; private set; }

    public decimal RefundableAmount => Amount - RefundedAmount;

    /// <summary>
    /// An approved card lands in <c>Authorized</c> with a hold that
    /// expires; approved Pix goes straight to <c>Captured</c> since there's
    /// no hold to place.
    /// </summary>
    public static Payment Authorize(
        Guid orderId,
        string customerId,
        decimal amount,
        string currency,
        string method,
        string shippingPostalPrefix,
        bool approved,
        DateTimeOffset decidedAt,
        TimeSpan authorizationWindow,
        string correlationId)
    {
        var requiresCapture = approved && PaymentMethods.RequiresCapture(method);

        return new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Method = method,
            ShippingPostalPrefix = shippingPostalPrefix,
            Approved = approved,
            DecidedAt = decidedAt,
            CorrelationId = correlationId,
            State = approved
                ? (requiresCapture ? PaymentMethods.PendingStateFor(method) : PaymentStates.Captured)
                : PaymentStates.Declined,
            AuthorizationExpiresAt = requiresCapture ? decidedAt + authorizationWindow : null,
            SettledAt = approved && !requiresCapture ? decidedAt : null
        };
    }

    /// <summary>Money promised but not yet moved: a card hold, or an issued boleto.</summary>
    private bool IsAwaitingSettlement =>
        State is PaymentStates.Authorized or PaymentStates.AwaitingPayment;

    /// <summary>
    /// Moves the money. False if the payment can't move from its current
    /// state, so a redelivered capture command is a no-op, not a double
    /// charge.
    /// </summary>
    public bool TryCapture(DateTimeOffset now)
    {
        if (!IsAwaitingSettlement)
        {
            return false;
        }

        State = PaymentStates.Captured;
        SettledAt = now;
        return true;
    }

    /// <summary>
    /// Gives back part or all of what was captured. The cumulative guard
    /// stops a redelivered refund, or the same units returned twice, from
    /// refunding more than was ever charged.
    /// </summary>
    public bool TryRefund(decimal amount, DateTimeOffset now)
    {
        if (State != PaymentStates.Captured && State != PaymentStates.Refunded)
        {
            return false;
        }

        if (amount <= 0m || amount > RefundableAmount)
        {
            return false;
        }

        RefundedAmount += amount;
        if (RefundedAmount >= Amount)
        {
            State = PaymentStates.Refunded;
        }

        SettledAt = now;
        return true;
    }

    /// <summary>
    /// Releases a hold that will never be charged - the order was
    /// cancelled, or the hold lapsed. Same guard as TryCapture: settling
    /// twice is impossible rather than merely unlikely.
    /// </summary>
    public bool TrySettleWithoutCapture(string state, string reason, DateTimeOffset now)
    {
        if (!IsAwaitingSettlement)
        {
            return false;
        }

        State = state;
        SettledAt = now;
        SettlementReason = reason;
        return true;
    }
}
