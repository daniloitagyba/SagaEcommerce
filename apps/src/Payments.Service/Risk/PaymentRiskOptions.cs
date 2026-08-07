using BuildingBlocks;

namespace Payments.Service.Risk;

/// <summary>
/// Milestone 66: the risk policy, in configuration so thresholds can be
/// tuned without a redeploy - real numbers get discovered from decline
/// rates, not reasoned out up front. Defaults keep this lab's scenarios
/// behaving recognisably: the README's deliberately-large order still
/// declines, now via HIGH_VALUE(50) + FIRST_PURCHASE(20).
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

    /// <summary>Milestone 73: how long a customer counts as new - the second order from a minutes-old account, unlike FIRST_PURCHASE's total absence of history.</summary>
    public int NewAccountWindowMinutes { get; init; } = 60;

    public int NewAccountScore { get; init; } = 25;

    /// <summary>Milestone 73: shipping somewhere new for this customer - ordinary on its own, so it scores low and matters only compounded.</summary>
    public int AddressMismatchScore { get; init; } = 20;

    /// <summary>Milestone 68: how long a card hold lasts before it lapses. Kept short (real acquirers give days) so the sweeper is observable in a lab session.</summary>
    public int AuthorizationWindowMinutes { get; init; } = 30;

    public TimeSpan AuthorizationWindow => TimeSpan.FromMinutes(AuthorizationWindowMinutes);

    /// <summary>Milestone 73: a boleto's due date - longer than the card hold but still watchable in a lab session (real ones run one to three days).</summary>
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
