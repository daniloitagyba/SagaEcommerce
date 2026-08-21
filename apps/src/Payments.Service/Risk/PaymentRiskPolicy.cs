namespace Payments.Service.Risk;

public sealed record PaymentHistoryEntry(
    Guid Id,
    decimal Amount,
    DateTimeOffset DecidedAt,
    bool Approved,
    string ShippingPostalPrefix);

public sealed record PaymentHistory(DateTimeOffset? FirstSeenAt, IReadOnlyList<PaymentHistoryEntry> Entries)
{
    public static readonly PaymentHistory Empty = new(null, []);
}

public sealed class PaymentRiskPolicy(PaymentRiskOptions options)
{
    public RiskAssessment Evaluate(
        string customerId,
        decimal amount,
        string shippingPostalPrefix,
        DateTimeOffset now,
        PaymentHistory history)
    {
        var signals = new List<RiskSignal>();

        if (amount > options.HighValueAmount)
        {
            signals.Add(new RiskSignal(
                "HIGH_VALUE",
                $"Amount {amount:0.00} is above the {options.HighValueAmount:0.00} review threshold",
                options.HighValueScore));
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Assess(signals);
        }

        if (history.FirstSeenAt is null)
        {
            signals.Add(new RiskSignal("FIRST_PURCHASE", "No previous payment for this customer", options.FirstPurchaseScore));
            return Assess(signals);
        }

        if (now - history.FirstSeenAt.Value < TimeSpan.FromMinutes(options.NewAccountWindowMinutes))
        {
            signals.Add(new RiskSignal(
                "NEW_ACCOUNT",
                $"First seen {(now - history.FirstSeenAt.Value).TotalMinutes:0} minutes ago",
                options.NewAccountScore));
        }

        var velocityWindowStart = now - TimeSpan.FromMinutes(options.VelocityWindowMinutes);
        var recentCount = history.Entries.Count(payment => payment.DecidedAt >= velocityWindowStart);
        if (recentCount >= options.VelocityOrderThreshold)
        {
            signals.Add(new RiskSignal(
                "VELOCITY",
                $"{recentCount} payments in the last {options.VelocityWindowMinutes} minutes",
                options.VelocityScore));
        }

        if (shippingPostalPrefix.Length > 0)
        {
            var knownPrefixes = history.Entries
                .Select(payment => payment.ShippingPostalPrefix)
                .Where(prefix => prefix.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            if (knownPrefixes.Count > 0 && !knownPrefixes.Contains(shippingPostalPrefix))
            {
                signals.Add(new RiskSignal(
                    "ADDRESS_MISMATCH",
                    $"Ships to {shippingPostalPrefix}; this customer has only shipped to {string.Join("/", knownPrefixes.Order(StringComparer.Ordinal))}",
                    options.AddressMismatchScore));
            }
        }

        var approvedHistory = history.Entries.Where(payment => payment.Approved).ToList();
        if (approvedHistory.Count > 0)
        {
            var average = approvedHistory.Average(payment => payment.Amount);
            if (average > 0m && amount > average * options.AtypicalAmountMultiplier)
            {
                signals.Add(new RiskSignal(
                    "ATYPICAL_AMOUNT",
                    $"Amount {amount:0.00} is more than {options.AtypicalAmountMultiplier}x this customer's {average:0.00} average",
                    options.AtypicalAmountScore));
            }
        }

        return Assess(signals);
    }

    private RiskAssessment Assess(IReadOnlyList<RiskSignal> signals)
    {
        var score = signals.Sum(signal => signal.Score);
        return new RiskAssessment(score, score < options.DeclineScoreThreshold, signals);
    }
}
