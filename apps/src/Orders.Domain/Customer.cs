namespace Orders.Domain;

/// <summary>A customer, auto-provisioned on first checkout rather than registered.</summary>
public sealed class Customer
{
    private Customer()
    {
    }

    public string Id { get; private set; } = string.Empty;

    /// <summary>Bronze on arrival; earned upward from lifetime spend.</summary>
    public string Tier { get; private set; } = CustomerTiers.Bronze;

    /// <summary>Counts only completed orders; cancelled or fully refunded ones do not count.</summary>
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

    /// <summary>Reverses a completed order's contribution to lifetime spend and order count; never demotes tier.</summary>
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
