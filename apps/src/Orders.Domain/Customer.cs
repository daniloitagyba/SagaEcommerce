namespace Orders.Domain;

/// <summary>
/// Milestone 71: a customer, at last.
///
/// Until now <c>CustomerId</c> was a bare string threaded through every
/// service - enough to group a payment history by, and not enough for
/// anything else. No account age, so the risk rules could not tell a
/// three-year customer from one created a minute ago. No address, so
/// shipping was a flat fee and tax was a single global rate. No standing
/// with the shop, so every promotion had to be a coupon somebody typed.
///
/// Auto-provisioned on first checkout rather than registered: this lab has
/// no sign-up flow, and inventing one would add a CRUD surface without
/// adding a distributed-systems concern. The first order a customer id
/// places creates the record, which is also what makes AccountAge a real
/// signal rather than a constant.
/// </summary>
public sealed class Customer
{
    private Customer()
    {
    }

    public string Id { get; private set; } = string.Empty;

    /// <summary>Bronze on arrival; earned upward from lifetime spend - see CustomerTiers.</summary>
    public string Tier { get; private set; } = CustomerTiers.Bronze;

    /// <summary>
    /// Counts only orders that actually completed. Cancelled and fully
    /// refunded orders must not buy standing - otherwise a customer could
    /// climb to Gold by placing and cancelling, which is the cheapest
    /// possible way to earn a permanent discount.
    /// </summary>
    public decimal LifetimeSpend { get; private set; }

    public int CompletedOrderCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Customer Create(string id, DateTimeOffset createdAt) =>
        new() { Id = id, Tier = CustomerTiers.Bronze, CreatedAt = createdAt, LifetimeSpend = 0m, CompletedOrderCount = 0 };

    /// <summary>Records a completed order and re-evaluates standing. Returns true when the tier actually moved.</summary>
    public bool RecordCompletedOrder(decimal amount)
    {
        LifetimeSpend += amount;
        CompletedOrderCount++;

        var earned = CustomerTiers.ForLifetimeSpend(LifetimeSpend);
        if (string.Equals(earned, Tier, StringComparison.Ordinal))
        {
            return false;
        }

        Tier = earned;
        return true;
    }

    /// <summary>
    /// Reverses a completed order's contribution - a full refund should not
    /// leave standing behind that the customer no longer paid for. Tier is
    /// deliberately <em>not</em> demoted here: taking a discount away
    /// retroactively is the kind of surprise that generates support
    /// tickets, and every real loyalty programme reviews downward on a
    /// schedule rather than on the instant.
    /// </summary>
    public void ReverseCompletedOrder(decimal amount)
    {
        LifetimeSpend = Math.Max(0m, LifetimeSpend - amount);
        CompletedOrderCount = Math.Max(0, CompletedOrderCount - 1);
    }
}

public static class CustomerTiers
{
    public const string Bronze = "Bronze";
    public const string Silver = "Silver";
    public const string Gold = "Gold";

    public const decimal SilverThreshold = 1_000m;
    public const decimal GoldThreshold = 5_000m;

    public static string ForLifetimeSpend(decimal lifetimeSpend) => lifetimeSpend switch
    {
        >= GoldThreshold => Gold,
        >= SilverThreshold => Silver,
        _ => Bronze
    };

    public static bool IsKnown(string tier) => tier is Bronze or Silver or Gold;

    /// <summary>Standing discount, applied to every order without a coupon being typed.</summary>
    public static decimal DiscountPercentageFor(string tier) => tier switch
    {
        Gold => 7m,
        Silver => 3m,
        _ => 0m
    };
}
