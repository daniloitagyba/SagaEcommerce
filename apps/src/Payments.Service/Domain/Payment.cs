using BuildingBlocks;

namespace Payments.Service.Domain;

public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>
    /// Milestone 66: without this, "has this customer paid before?" and
    /// "is this order out of character for them?" are unanswerable, and
    /// the payment decision can only ever be a function of the amount in
    /// front of it - which is exactly what it was until now.
    /// </summary>
    public string CustomerId { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// The risk decision. Kept alongside <see cref="State"/> rather than
    /// derived from it because the two answer different questions - "did
    /// we agree to take this money?" and "where is the money now?" - and
    /// the choreographed <c>PaymentDecided</c> contract has carried this
    /// exact flag since Milestone 12.
    /// </summary>
    public bool Approved { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Milestone 68: Card or Pix - see PaymentMethods for why this changes the flow.</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>
    /// Milestone 73: where this order shipped, kept so the next order can
    /// be compared against it. Stored on the payment rather than looked up
    /// from Orders because Payments must be able to score a decision
    /// without a synchronous call into another service's database.
    /// </summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    /// <summary>Milestone 68: see PaymentStates.</summary>
    public string State { get; private set; } = string.Empty;

    /// <summary>
    /// When a card hold lapses if nobody captures it. Null for anything
    /// that was never authorized - a declined payment or an instant Pix.
    /// </summary>
    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string? SettlementReason { get; private set; }

    /// <summary>
    /// Milestone 70: how much of a captured payment has been given back.
    ///
    /// Cumulative rather than a boolean because returns are partial by
    /// nature - a customer keeps the book and sends back two of three
    /// shirts - so a payment can be refunded several times before it is
    /// fully refunded.
    /// </summary>
    public decimal RefundedAmount { get; private set; }

    public decimal RefundableAmount => Amount - RefundedAmount;

    /// <summary>
    /// Milestone 68: replaces Decide. An approved card lands in
    /// <c>Authorized</c> with a hold that expires; approved Pix goes
    /// straight to <c>Captured</c> because there is no hold to place.
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

    /// <summary>
    /// Moves the money. Returns false when the payment is not in a state
    /// money can move from - already captured, already voided, expired, or
    /// declined outright - which makes a redelivered capture command a
    /// no-op rather than a double charge.
    /// </summary>
    /// <summary>
    /// Money is promised but not yet moved: a card hold, or an issued
    /// boleto. Both are settled by the same commands, so every guard asks
    /// this rather than naming one state and quietly excluding the other.
    /// </summary>
    private bool IsAwaitingSettlement =>
        State is PaymentStates.Authorized or PaymentStates.AwaitingPayment;

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
    /// Milestone 70: gives back part (or all) of what was captured.
    ///
    /// Only a captured payment can be refunded - money that was never taken
    /// cannot be returned, and an authorization that was voided or expired
    /// was released rather than charged. The cumulative guard is what stops
    /// a redelivered refund command, or a customer returning the same units
    /// twice, from refunding more than was ever charged.
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
