using BuildingBlocks;

namespace Payments.Service.Domain;

public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Identifies the single payment that owns this order's lifecycle.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Lets the risk decision weigh this customer's history, not just the amount.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>The risk decision, kept alongside <see cref="State"/> rather than derived from it.</summary>
    public bool Approved { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Card or Pix - see PaymentMethods for why this changes the flow.</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>Where this order shipped, stored here so the next order can be scored without a synchronous cross-service call.</summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public string State { get; private set; } = string.Empty;

    /// <summary>When a card hold lapses if nobody captures it; null for anything never authorized.</summary>
    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string? SettlementReason { get; private set; }

    /// <summary>How much of a captured payment has been given back, cumulative across several partial refunds.</summary>
    public decimal RefundedAmount { get; private set; }

    public decimal RefundableAmount => Amount - RefundedAmount;

    /// <summary>An approved card lands in <c>Authorized</c> with an expiring hold; approved Pix goes straight to <c>Captured</c>.</summary>
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
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Customer and correlation identifiers are required.");
        }

        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required.", nameof(currency));
        }

        if (!PaymentMethods.IsSupported(method))
        {
            throw new ArgumentException("Payment method is not supported.", nameof(method));
        }

        if (approved && PaymentMethods.RequiresCapture(method) && authorizationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationWindow), "Authorization window must be positive.");
        }

        var requiresCapture = approved && PaymentMethods.RequiresCapture(method);

        return new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            IsPrimary = true,
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

    /// <summary>Moves the money; false if the payment can't move from its current state, so a redelivered capture is a no-op.</summary>
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

    /// <summary>Gives back part or all of what was captured; the cumulative guard prevents refunding more than was ever charged.</summary>
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

    /// <summary>Releases a hold that will never be charged; settling twice is impossible, not merely unlikely.</summary>
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

    /// <summary>Ensures a cancelled order's payment owes nothing: voids an open hold, or refunds captured money in full.</summary>
    public bool TryCancel(string reason, DateTimeOffset now)
    {
        if (IsAwaitingSettlement)
        {
            return TrySettleWithoutCapture(PaymentStates.Voided, reason, now);
        }

        if (State == PaymentStates.Captured)
        {
            return TryRefund(RefundableAmount, now);
        }

        return false;
    }
}
