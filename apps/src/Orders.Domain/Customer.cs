namespace Orders.Domain;

/// <summary>
/// Milestone 71: a customer, at last - until now <c>CustomerId</c> was a
/// bare string, enough to group payment history by and not enough for
/// account age, address, or standing with the shop. Auto-provisioned on
/// first checkout rather than registered, since this lab has no sign-up
/// flow and inventing one would add CRUD, not a distributed-systems concern.
/// </summary>
public sealed class Customer
{
    private Customer()
    {
    }

    public string Id { get; private set; } = string.Empty;

    /// <summary>Bronze on arrival; earned upward from lifetime spend - see CustomerTiers.</summary>
    public string Tier { get; private set; } = CustomerTiers.Bronze;

    /// <summary>Counts only completed orders - cancelled or fully refunded ones must not buy standing.</summary>
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
    /// Reverses a completed order's contribution after a full refund. Tier
    /// is deliberately <em>not</em> demoted here - taking a discount away
    /// retroactively generates support tickets; real loyalty programmes
    /// review downward on a schedule, not on the instant.
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
