using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;

namespace Payments.Service.Risk;

public sealed record RiskSignal(string Code, string Description, int Score);

public sealed record RiskAssessment(int Score, bool Approved, IReadOnlyList<RiskSignal> Signals)
{
    public string ReasonSummary => Signals.Count == 0
        ? "no risk signals"
        : string.Join("; ", Signals.Select(signal => $"{signal.Code}(+{signal.Score})"));
}

/// <summary>
/// Milestone 66: replaces "decline anything over 1000" with a scored set of
/// signals.
///
/// The old rule was a deterministic stub, and it made the payment step a
/// pure function of one number - which meant no amount of saga machinery
/// around it was ever exercising a decision that could depend on anything
/// else. These signals are the ones a real processor actually starts with,
/// and crucially they depend on <em>state</em>: what this customer has
/// bought before, and how fast they are buying right now. That makes the
/// payment step genuinely stateful, which in turn makes the inbox
/// deduplication on both saga paths matter for a reason beyond tidiness -
/// a replayed message that re-ran this evaluation would see its own
/// earlier write in the history and could reach a different answer.
///
/// Scored rather than boolean because signals compound: a first purchase
/// is unremarkable, and a large first purchase from an account placing its
/// third order in five minutes is not.
/// </summary>
public sealed class PaymentRiskEvaluator(PaymentsDbContext dbContext, IOptions<PaymentRiskOptions> options)
{
    private readonly PaymentRiskOptions _options = options.Value;

    public async Task<RiskAssessment> EvaluateAsync(
        string customerId,
        decimal amount,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await EvaluateAsync(customerId, amount, string.Empty, now, cancellationToken);

    public async Task<RiskAssessment> EvaluateAsync(
        string customerId,
        decimal amount,
        string shippingPostalPrefix,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var signals = new List<RiskSignal>();

        if (amount > _options.HighValueAmount)
        {
            signals.Add(new RiskSignal(
                "HIGH_VALUE",
                $"Amount {amount:0.00} is above the {_options.HighValueAmount:0.00} review threshold",
                _options.HighValueScore));
        }

        // An unknown customer is treated as unknown rather than as a new
        // one: a decision request that lost its CustomerId (see
        // PaymentDecisionRequested) should not be scored as a first
        // purchase for every order that hits it.
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Assess(signals);
        }

        var history = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.CustomerId == customerId)
            .Select(payment => new { payment.Amount, payment.DecidedAt, payment.Approved, payment.ShippingPostalPrefix })
            .ToListAsync(cancellationToken);

        if (history.Count == 0)
        {
            signals.Add(new RiskSignal("FIRST_PURCHASE", "No previous payment for this customer", _options.FirstPurchaseScore));
            return Assess(signals);
        }

        var velocityWindowStart = now - TimeSpan.FromMinutes(_options.VelocityWindowMinutes);
        var recentCount = history.Count(payment => payment.DecidedAt >= velocityWindowStart);
        if (recentCount >= _options.VelocityOrderThreshold)
        {
            signals.Add(new RiskSignal(
                "VELOCITY",
                $"{recentCount} payments in the last {_options.VelocityWindowMinutes} minutes",
                _options.VelocityScore));
        }

        // An account that first appeared minutes ago and is already placing
        // another order is not a returning customer. FIRST_PURCHASE cannot
        // express this - by definition it has already stopped firing.
        var firstSeen = history.Min(payment => payment.DecidedAt);
        if (now - firstSeen < TimeSpan.FromMinutes(_options.NewAccountWindowMinutes))
        {
            signals.Add(new RiskSignal(
                "NEW_ACCOUNT",
                $"First seen {(now - firstSeen).TotalMinutes:0} minutes ago",
                _options.NewAccountScore));
        }

        // Only meaningful against a customer who has shipped somewhere
        // before: an unknown destination, or a customer with none on
        // record, is missing information rather than evidence. Scoring
        // absence as mismatch would flag every order placed without an
        // address - which, before Milestone 71, was all of them.
        if (shippingPostalPrefix.Length > 0)
        {
            var knownPrefixes = history
                .Select(payment => payment.ShippingPostalPrefix)
                .Where(prefix => prefix.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            if (knownPrefixes.Count > 0 && !knownPrefixes.Contains(shippingPostalPrefix))
            {
                signals.Add(new RiskSignal(
                    "ADDRESS_MISMATCH",
                    $"Ships to {shippingPostalPrefix}; this customer has only shipped to {string.Join("/", knownPrefixes.Order(StringComparer.Ordinal))}",
                    _options.AddressMismatchScore));
            }
        }

        var approvedHistory = history.Where(payment => payment.Approved).ToList();
        if (approvedHistory.Count > 0)
        {
            var average = approvedHistory.Average(payment => payment.Amount);
            if (average > 0m && amount > average * _options.AtypicalAmountMultiplier)
            {
                signals.Add(new RiskSignal(
                    "ATYPICAL_AMOUNT",
                    $"Amount {amount:0.00} is more than {_options.AtypicalAmountMultiplier}x this customer's {average:0.00} average",
                    _options.AtypicalAmountScore));
            }
        }

        return Assess(signals);
    }

    private RiskAssessment Assess(List<RiskSignal> signals)
    {
        var score = signals.Sum(signal => signal.Score);
        return new RiskAssessment(score, score < _options.DeclineScoreThreshold, signals);
    }
}
