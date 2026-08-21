using NodaMoney;

namespace Orders.Domain.Pricing;

/// <summary>A line as the pricing engine sees it; uses identity equality since it is an NRules fact.</summary>
public sealed record PricingLine(
    string Sku,
    string ProductName,
    string CategorySlug,
    int Quantity,
    Money UnitPrice)
{
    public Money LineSubtotal => UnitPrice * Quantity;

    public bool Equals(PricingLine? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>A coupon already looked up and found eligible, passed in resolved so pricing stays deterministic.</summary>
public sealed record ResolvedCoupon(string Code, string Description, decimal Percentage, string? ExclusivityGroup = null);

/// <summary>A campaign already looked up and found eligible; Amount is a flat value, known before pricing runs.</summary>
public sealed record ResolvedCampaign(string Code, string Description, decimal Amount, string? ExclusivityGroup);

/// <summary>Who's buying, resolved before pricing so the rules stay a pure function of the facts handed to them.</summary>
public sealed record PricingCustomer(string CustomerId, string Tier, DateTimeOffset AccountCreatedAt);

public sealed record PricingDestination(string Region, string PostalPrefix);

public sealed record PricingRequest(
    string CustomerId,
    Currency Currency,
    IReadOnlyList<PricingLine> Lines,
    ResolvedCoupon? Coupon = null,
    PricingCustomer? Customer = null,
    PricingDestination? Destination = null,
    /// <summary>The best currently-active, still-funded campaign, if any.</summary>
    ResolvedCampaign? Campaign = null,
    /// <summary>The instant every promotion's validity window is checked against; callers that omit it get a deterministic epoch rather than an ambient clock.</summary>
    DateTimeOffset? EvaluatedAt = null)
{
    public DateTimeOffset EffectiveEvaluatedAt => EvaluatedAt ?? DateTimeOffset.UnixEpoch;

    public Money Subtotal => Lines.Aggregate(
        new Money(0m, Currency),
        (running, line) => running + line.LineSubtotal);

    public int TotalQuantity => Lines.Sum(line => line.Quantity);
}

/// <summary>One discount a rule granted, itemised so the receipt can say why; uses identity equality like PricingLine.</summary>
public sealed record AppliedDiscount(string Code, string Description, Money Amount, string? ExclusivityGroup = null)
{
    public bool Equals(AppliedDiscount? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>A charge added rather than deducted, itemised so the total stays explainable.</summary>
public sealed record AppliedCharge(string Code, string Description, Money Amount);

/// <summary>The priced result; GrandTotal is computed here and per-line allocations are guaranteed to sum exactly to their totals.</summary>
public sealed record PricingBreakdown(
    Money Subtotal,
    IReadOnlyList<AppliedDiscount> Discounts,
    IReadOnlyList<AppliedCharge> Charges,
    Money DiscountTotal,
    Money ShippingTotal,
    Money TaxTotal,
    Money GrandTotal,
    IReadOnlyList<Money> LineDiscounts,
    IReadOnlyList<Money> LineTaxes);

public static class PricingAllocation
{
    /// <summary>Spreads an order-level discount across the lines proportionally to each line's subtotal.</summary>
    public static IReadOnlyList<Money> AllocateDiscounts(
        Money discountTotal,
        IReadOnlyList<PricingLine> lines,
        Currency currency)
    {
        var zero = new Money(0m, currency);

        if (lines.Count == 0)
        {
            return [];
        }

        if (discountTotal == zero)
        {
            return [.. lines.Select(_ => zero)];
        }

        var weights = lines
            .Select(line => (long)decimal.Round(line.LineSubtotal.Amount * 100m, 0, MidpointRounding.AwayFromZero))
            .ToArray();

        return MoneyAllocation.Allocate(discountTotal, weights, currency);
    }

    /// <summary>Spreads the order's tax across lines weighted by each line's discounted value, not its raw subtotal.</summary>
    public static IReadOnlyList<Money> AllocateTax(
        Money taxTotal,
        IReadOnlyList<PricingLine> lines,
        IReadOnlyList<Money> lineDiscounts,
        Currency currency)
    {
        var zero = new Money(0m, currency);

        if (lines.Count == 0)
        {
            return [];
        }

        if (taxTotal == zero)
        {
            return [.. lines.Select(_ => zero)];
        }

        var weights = lines
            .Select((line, index) => (long)decimal.Round(
                (line.LineSubtotal.Amount - lineDiscounts[index].Amount) * 100m, 0, MidpointRounding.AwayFromZero))
            .ToArray();

        return MoneyAllocation.Allocate(taxTotal, weights, currency);
    }
}

/// <summary>The domain's contract for pricing an order; the NRules-backed implementation lives in Orders.Application.</summary>
public interface IPricingEngine
{
    PricingBreakdown Price(PricingRequest request);
}
