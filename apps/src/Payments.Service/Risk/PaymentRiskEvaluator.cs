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

/// <summary>Scores a payment decision from signals dependent on the customer's history and buying pace.</summary>
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

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Assess(signals);
        }

        DateTimeOffset? firstSeen;
        if (string.Equals(
                dbContext.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
        {
            var decidedDates = await dbContext.Payments
                .AsNoTracking()
                .Where(payment => payment.IsPrimary && payment.CustomerId == customerId)
                .Select(payment => payment.DecidedAt)
                .ToListAsync(cancellationToken);
            firstSeen = decidedDates.Count > 0 ? decidedDates.Min() : null;
        }
        else
        {
            firstSeen = await dbContext.Payments
                .AsNoTracking()
                .Where(payment => payment.IsPrimary && payment.CustomerId == customerId)
                .Select(payment => (DateTimeOffset?)payment.DecidedAt)
                .MinAsync(cancellationToken);
        }

        if (firstSeen is null)
        {
            signals.Add(new RiskSignal("FIRST_PURCHASE", "No previous payment for this customer", _options.FirstPurchaseScore));
            return Assess(signals);
        }

        if (now - firstSeen.Value < TimeSpan.FromMinutes(_options.NewAccountWindowMinutes))
        {
            signals.Add(new RiskSignal(
                "NEW_ACCOUNT",
                $"First seen {(now - firstSeen.Value).TotalMinutes:0} minutes ago",
                _options.NewAccountScore));
        }

        var historyQuery = dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.IsPrimary && payment.CustomerId == customerId)
            .Select(payment => new PaymentHistoryRow(
                payment.Id,
                payment.Amount,
                payment.DecidedAt,
                payment.Approved,
                payment.ShippingPostalPrefix));

        IReadOnlyList<PaymentHistoryRow> history;
        if (string.Equals(
                dbContext.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
        {
            history = (await historyQuery.ToListAsync(cancellationToken))
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)
                .ToList();
        }
        else
        {
            history = await historyQuery
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)
                .ToListAsync(cancellationToken);
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

    private sealed record PaymentHistoryRow(
        Guid Id,
        decimal Amount,
        DateTimeOffset DecidedAt,
        bool Approved,
        string ShippingPostalPrefix);
}
