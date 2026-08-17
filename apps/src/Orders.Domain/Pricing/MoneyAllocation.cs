using NodaMoney;

namespace Orders.Domain.Pricing;

/// <summary>Splits an amount of money into shares that are exact and never negative, via cumulative floor division.</summary>
public static class MoneyAllocation
{
    /// <summary>Allocates the total across shares weighted by the given weights; an all-zero weight vector splits evenly.</summary>
    public static IReadOnlyList<Money> Allocate(Money total, IReadOnlyList<long> weights, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.Count == 0)
        {
            return [];
        }

        var effective = weights.Any(weight => weight > 0) ? weights : [.. weights.Select(_ => 1L)];
        var totalWeight = effective.Sum();
        var totalMinor = ToMinorUnits(total.Amount);

        var shares = new Money[effective.Count];
        long cumulativeWeight = 0;
        long previousCumulative = 0;

        for (var index = 0; index < effective.Count; index++)
        {
            cumulativeWeight += effective[index];

            var cumulative = totalMinor * cumulativeWeight / totalWeight;
            shares[index] = FromMinorUnits(cumulative - previousCumulative, currency);
            previousCumulative = cumulative;
        }

        return shares;
    }

    /// <summary>Splits into <paramref name="shares"/> equal parts, remainder distributed to the later ones.</summary>
    public static IReadOnlyList<Money> Split(Money total, int shares, Currency currency)
    {
        if (shares <= 0)
        {
            return [];
        }

        return Allocate(total, [.. Enumerable.Repeat(1L, shares)], currency);
    }

    /// <summary>The cumulative amount owed after the given number of units out of totalUnits.</summary>
    public static Money CumulativeFor(Money total, int totalUnits, int units, Currency currency)
    {
        if (totalUnits <= 0 || units <= 0)
        {
            return new Money(0m, currency);
        }

        var clamped = Math.Min(units, totalUnits);
        var totalMinor = ToMinorUnits(total.Amount);
        return FromMinorUnits(totalMinor * clamped / totalUnits, currency);
    }

    private static long ToMinorUnits(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits / 100m, currency);
}
