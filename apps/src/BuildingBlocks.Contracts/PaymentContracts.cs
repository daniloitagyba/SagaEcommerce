namespace BuildingBlocks;

/// <summary>How the shopper is paying, and whether the two-phase authorize/capture flow applies.</summary>
public static class PaymentMethods
{
    /// <summary>Two-phase: authorize a hold now, capture when the goods ship.</summary>
    public const string Card = "Card";

    /// <summary>Single-phase: an instant transfer, captured the moment it is approved.</summary>
    public const string Pix = "Pix";

    /// <summary>A printed slip the shopper pays at a bank, days later or never.</summary>
    public const string Boleto = "Boleto";

    public static bool IsSupported(string? method) =>
        method is Card or Pix or Boleto;

    /// <summary>Whether the money still has to be moved by a later, explicit step.</summary>
    public static bool RequiresCapture(string method) =>
        method is Card or Boleto;

    /// <summary>The state an approved payment starts in.</summary>
    public static string PendingStateFor(string method) =>
        string.Equals(method, Boleto, StringComparison.Ordinal)
            ? PaymentStates.AwaitingPayment
            : PaymentStates.Authorized;
}

/// <summary>The life cycle states of a payment, replacing a single Approved boolean.</summary>
public static class PaymentStates
{
    public const string Declined = "Declined";
    public const string Authorized = "Authorized";

    /// <summary>A boleto has been issued and not yet paid.</summary>
    public const string AwaitingPayment = "AwaitingPayment";
    public const string Captured = "Captured";
    public const string Voided = "Voided";
    public const string Expired = "Expired";

    /// <summary>Fully refunded after a return. A partially refunded payment stays Captured.</summary>
    public const string Refunded = "Refunded";

    /// <summary>An authorization is the only state money can still be moved from.</summary>
    public static bool IsSettled(string state) =>
        state is Declined or Captured or Voided or Expired or Refunded;
}

/// <summary>Orders asks Payments to actually charge an authorization it is holding.</summary>
public sealed record PaymentCaptureRequested(
    Guid OrderId,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>The order was cancelled; settle whatever this payment actually is, whichever way that requires.</summary>
public sealed record PaymentCancellationRequested(
    Guid OrderId,
    string Reason,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>The outcome of a capture, cancellation, refund, or the authorization sweep's bulk expiry.</summary>
public sealed record PaymentSettlementReplied(
    Guid OrderId,
    Guid PaymentId,
    string State,
    decimal Amount,
    string Currency,
    string CorrelationId,
    DateTimeOffset SettledAt,
    bool RequiresReconciliation = false);
