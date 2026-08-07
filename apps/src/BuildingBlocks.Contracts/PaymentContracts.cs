namespace BuildingBlocks;

/// <summary>
/// How the shopper is paying, and whether the two-phase authorize/capture
/// flow applies at all: a card is <em>authorized</em> then later
/// <em>captured</em>; Pix is an immediate transfer with nothing to capture.
/// A single "approved: true" boolean can't express either distinction.
/// </summary>
public static class PaymentMethods
{
    /// <summary>Two-phase: authorize a hold now, capture when the goods ship.</summary>
    public const string Card = "Card";

    /// <summary>Single-phase: an instant transfer, captured the moment it is approved.</summary>
    public const string Pix = "Pix";

    /// <summary>
    /// A printed slip the shopper pays at a bank, days later or never.
    /// Two-phase like a card, but for the opposite reason - no hold exists,
    /// only an unpaid invoice - which is why it doesn't share Card's
    /// <c>Authorized</c> state.
    /// </summary>
    public const string Boleto = "Boleto";

    public static bool IsSupported(string? method) =>
        method is Card or Pix or Boleto;

    /// <summary>
    /// Whether the money still has to be moved by a later, explicit step.
    /// True for a card (capture the hold) and a boleto (the shopper pays
    /// the slip); false for Pix, which is already settled on approval.
    /// </summary>
    public static bool RequiresCapture(string method) =>
        method is Card or Boleto;

    /// <summary>
    /// The state an approved payment starts in. A card holds someone's
    /// money; a boleto holds nothing and merely waits.
    /// </summary>
    public static string PendingStateFor(string method) =>
        string.Equals(method, Boleto, StringComparison.Ordinal)
            ? PaymentStates.AwaitingPayment
            : PaymentStates.Authorized;
}

/// <summary>
/// Milestone 68: the life of a payment, replacing the single
/// <c>Approved</c> boolean.
///
///   Declined                                       (risk rules said no - terminal)
///   Captured                                       (Pix: approved is the same instant as paid)
///   Authorized     ──capture──► Captured           (Card: the money moves when the order ships)
///                  ──void────► Voided              (the order was cancelled)
///                  ──expire──► Expired             (nobody ever captured; the hold lapsed)
///   AwaitingPayment──capture──► Captured           (Boleto: the shopper paid the slip)
///                  ──void────► Voided              (the order was cancelled before payment)
///                  ──expire──► Expired             (the slip's due date passed unpaid)
/// </summary>
public static class PaymentStates
{
    public const string Declined = "Declined";
    public const string Authorized = "Authorized";

    /// <summary>
    /// Milestone 73: a boleto has been issued and not yet paid. Distinct
    /// from <see cref="Authorized"/> because no money is held - if this
    /// expires, nothing is released, the shopper simply never paid.
    /// </summary>
    public const string AwaitingPayment = "AwaitingPayment";
    public const string Captured = "Captured";
    public const string Voided = "Voided";
    public const string Expired = "Expired";

    /// <summary>Milestone 70: fully refunded after a return. A partially refunded payment stays Captured.</summary>
    public const string Refunded = "Refunded";

    /// <summary>An authorization is the only state money can still be moved from.</summary>
    public static bool IsSettled(string state) =>
        state is Declined or Captured or Voided or Expired or Refunded;
}

/// <summary>
/// Orders asks Payments to actually charge an authorization it is holding.
/// Plain JSON, like the saga's other command/reply pairs - an internal
/// request between two known parties, not a domain event others evolve
/// against independently.
/// </summary>
public sealed record PaymentCaptureRequested(
    Guid OrderId,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>Orders asks Payments to release a hold it will never charge.</summary>
public sealed record PaymentVoidRequested(
    Guid OrderId,
    string Reason,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>
/// The outcome of a capture or void. One reply type for both so the
/// consumer has a single shape to handle - <see cref="State"/> says which
/// terminal state the payment actually reached.
/// </summary>
public sealed record PaymentSettlementReplied(
    Guid OrderId,
    Guid PaymentId,
    string State,
    decimal Amount,
    string Currency,
    string CorrelationId,
    DateTimeOffset SettledAt);
