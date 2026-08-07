using BuildingBlocks;

namespace Payments.Service.Risk;

/// <summary>
/// Milestone 66: the risk policy, in configuration so thresholds can be
/// tuned without a redeploy - which is how these are actually operated in
/// practice, since the right numbers are discovered from decline rates
/// rather than reasoned out up front.
///
/// The defaults are chosen so this lab's existing scenarios keep behaving
/// recognisably: a single ordinary order is approved, and the README's
/// deliberately-large order still declines - it now scores HIGH_VALUE(50)
/// plus FIRST_PURCHASE(20) rather than tripping a bare amount comparison.
/// </summary>
public sealed class PaymentRiskOptions
{
    public const string SectionName = "PaymentRisk";

    /// <summary>A decision scoring at or above this is declined.</summary>
    public int DeclineScoreThreshold { get; init; } = 60;

    public decimal HighValueAmount { get; init; } = 1_000m;

    public int HighValueScore { get; init; } = 50;

    public int FirstPurchaseScore { get; init; } = 20;

    public int VelocityWindowMinutes { get; init; } = 5;

    public int VelocityOrderThreshold { get; init; } = 3;

    public int VelocityScore { get; init; } = 35;

    /// <summary>How many times the customer's own average counts as out of character.</summary>
    public decimal AtypicalAmountMultiplier { get; init; } = 5m;

    public int AtypicalAmountScore { get; init; } = 30;

    /// <summary>
    /// Milestone 73: how long a customer counts as new. Distinct from
    /// FIRST_PURCHASE, which is the absence of any history at all - this is
    /// the second order from an account that only appeared minutes ago, a
    /// pattern that looks nothing like a returning shopper.
    /// </summary>
    public int NewAccountWindowMinutes { get; init; } = 60;

    public int NewAccountScore { get; init; } = 25;

    /// <summary>
    /// Milestone 73: shipping somewhere this customer has never shipped
    /// before. On its own it is ordinary - people move, and send gifts - so
    /// it scores low and only matters when it compounds with something
    /// else.
    /// </summary>
    public int AddressMismatchScore { get; init; } = 20;

    /// <summary>
    /// Milestone 68: how long a card authorization is held before it lapses
    /// if nobody captures it. Real acquirers give days; kept short here so
    /// the expiry sweeper is observable in a lab session rather than
    /// theoretical.
    /// </summary>
    public int AuthorizationWindowMinutes { get; init; } = 30;

    public TimeSpan AuthorizationWindow => TimeSpan.FromMinutes(AuthorizationWindowMinutes);

    /// <summary>
    /// Milestone 73: a boleto's due date. Real ones run one to three days;
    /// kept to a longer-than-card-but-still-observable window here for the
    /// same reason the card hold is 30 minutes rather than a week - the
    /// expiry sweeper has to be watchable inside a lab session.
    /// </summary>
    public int BoletoWindowMinutes { get; init; } = 120;

    /// <summary>
    /// How long an approved payment of this method may sit unsettled. Pix
    /// never gets here - it is captured on approval - so its value is
    /// irrelevant rather than wrong.
    /// </summary>
    public TimeSpan SettlementWindowFor(string method) =>
        string.Equals(method, PaymentMethods.Boleto, StringComparison.Ordinal)
            ? TimeSpan.FromMinutes(BoletoWindowMinutes)
            : AuthorizationWindow;
}
