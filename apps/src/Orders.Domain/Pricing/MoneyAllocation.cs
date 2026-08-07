using NodaMoney;

namespace Orders.Domain.Pricing;

/// <summary>
/// Milestone 70: splits an amount of money into shares that are exact
/// <em>and</em> never negative - replacing NodaMoney's <c>Split</c>, which
/// sums back to the total correctly but can hand out a negative share (e.g.
/// <c>Money(0.06, BRL).Split(11)</c> → ten shares of 0.01 then -0.04),
/// measured at roughly 1-in-1,000 for <c>Split(int)</c>. A negative discount
/// share raises a line's price; a negative refund share overrefunds it.
/// Fixed here via cumulative floor division over integer minor units: each
/// share is the difference between two points on a non-decreasing curve, so
/// none can be negative, and the last cumulative value is the total exactly.
/// </summary>
public static class MoneyAllocation
{
    /// <summary>
    /// Allocates <paramref name="total"/> across shares weighted by
    /// <paramref name="weights"/>. Weights must be non-negative; an
    /// all-zero weight vector splits evenly.
    /// </summary>
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

            // Floor of (total * cumulativeWeight / totalWeight): monotone, so >= 0, exact at the end.
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

    /// <summary>
    /// The cumulative amount owed after <paramref name="units"/> of
    /// <paramref name="totalUnits"/>, exposed directly so partial returns
    /// can be priced as a difference between two points, not every share.
    /// </summary>
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

    // Assumes minor-unit-2; every currency this lab handles is BRL.
    private static long ToMinorUnits(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits / 100m, currency);
}
