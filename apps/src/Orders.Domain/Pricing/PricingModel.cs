using NodaMoney;

namespace Orders.Domain.Pricing;

/// <summary>
/// A line as the pricing engine sees it, plus its category for
/// category-targeted promotions.
///
/// Identity equality, not the record default: this is an NRules fact, and
/// two lines that happen to share SKU/quantity/price - a real cart shape -
/// would otherwise compare equal and crash the second insert
/// ("Facts for insert already exist"). They're still two separate lines.
/// </summary>
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

/// <summary>
/// A coupon already looked up and found eligible - passed in resolved,
/// not as a code, so no rule reaches into the database mid-evaluation and
/// makes pricing non-deterministic.
/// </summary>
public sealed record ResolvedCoupon(string Code, string Description, decimal Percentage);

/// <summary>
/// Who's buying, resolved before pricing for the same reason the coupon
/// is - the rules stay a pure function of the facts handed to them.
/// </summary>
public sealed record PricingCustomer(string CustomerId, string Tier, DateTimeOffset AccountCreatedAt);

public sealed record PricingDestination(string Region, string PostalPrefix);

public sealed record PricingRequest(
    string CustomerId,
    Currency Currency,
    IReadOnlyList<PricingLine> Lines,
    ResolvedCoupon? Coupon = null,
    PricingCustomer? Customer = null,
    PricingDestination? Destination = null)
{
    public Money Subtotal => Lines.Aggregate(
        new Money(0m, Currency),
        (running, line) => running + line.LineSubtotal);

    public int TotalQuantity => Lines.Sum(line => line.Quantity);
}

/// <summary>
/// One discount a rule granted - itemised, not collapsed into one number,
/// so the receipt can say <em>why</em> ("10% coupon SAVE10", "5% off
/// electronics").
///
/// Identity equality, same reason as PricingLine: two lines of the same
/// SKU and quantity can legitimately earn a Code/Description/Amount that
/// all match, and value equality would make the second grant look like a
/// duplicate of the first and lose it.
/// </summary>
public sealed record AppliedDiscount(string Code, string Description, Money Amount)
{
    public bool Equals(AppliedDiscount? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
/// A charge added rather than deducted - shipping and tax. Same reasoning
/// as AppliedDiscount: itemised so the total stays explainable.
/// </summary>
public sealed record AppliedCharge(string Code, string Description, Money Amount);

/// <summary>
/// The priced result. GrandTotal is computed here, never supplied, and the
/// per-line discount allocation is guaranteed to sum to exactly
/// DiscountTotal - see AllocateDiscounts. LineTaxes is the same guarantee
/// for TaxTotal (Milestone 82) - stored per line at checkout time, same
/// reasoning as LineDiscounts, so a later return can refund a line's tax
/// share without re-deriving it from a rate that may have changed since.
/// </summary>
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
    /// <summary>
    /// Spreads an order-level discount across the lines proportionally to
    /// each line's subtotal. Naive rounding loses a centavo (three lines
    /// sharing R$10,00 each get R$3,33); NodaMoney's Split (M66) sums
    /// correctly but can hand back a <em>negative</em> share (M70, ~1 in
    /// 200k) - see MoneyAllocation.Allocate for the fix.
    /// </summary>
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

        // Weighted by each line's subtotal in minor units. A line that is
        // genuinely free carries no weight; an order made entirely of free
        // lines falls back to an even split, handled inside Allocate.
        var weights = lines
            .Select(line => (long)decimal.Round(line.LineSubtotal.Amount * 100m, 0, MidpointRounding.AwayFromZero))
            .ToArray();

        return MoneyAllocation.Allocate(discountTotal, weights, currency);
    }

    /// <summary>
    /// Milestone 82: spreads the order's tax across lines weighted by each
    /// line's <em>discounted</em> value, not its raw subtotal - tax itself
    /// is computed on the discounted subtotal (see NRulesPricingEngine), so
    /// a heavily-discounted line should carry proportionally less of it.
    /// <paramref name="lineDiscounts"/> must already be aligned with
    /// <paramref name="lines"/>, index for index, the same list
    /// <see cref="AllocateDiscounts"/> returns.
    /// </summary>
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

        // Each line's discounted value can never be negative: LineDiscounts
        // is itself an allocation of an order-level discount already capped
        // at the subtotal, so no line's share of it can exceed that line's
        // own subtotal.
        var weights = lines
            .Select((line, index) => (long)decimal.Round(
                (line.LineSubtotal.Amount - lineDiscounts[index].Amount) * 100m, 0, MidpointRounding.AwayFromZero))
            .ToArray();

        return MoneyAllocation.Allocate(taxTotal, weights, currency);
    }
}

/// <summary>
/// The domain's contract for pricing an order. The NRules-backed
/// implementation lives in Orders.Application, so the domain never depends
/// on a rules engine - enforced by an architecture fitness function.
/// </summary>
public interface IPricingEngine
{
    PricingBreakdown Price(PricingRequest request);
}
