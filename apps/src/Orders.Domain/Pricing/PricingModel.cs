using NodaMoney;

namespace Orders.Domain.Pricing;

/// <summary>
/// A line as the pricing engine sees it: the SKU, its category (several
/// promotions target a category rather than a specific product), how many,
/// and the unit price read from the live catalog at checkout.
///
/// Equality is identity, not the record default of value equality - this
/// type is inserted into an NRules working memory as a fact, one per line,
/// and NRules refuses to insert a fact that already compares equal to one
/// already in the session (System.ArgumentException: "Facts for insert
/// already exist"). Two order lines that happen to name the same SKU,
/// quantity and price - a real cart shape, not a contrived one - would
/// otherwise crash pricing entirely on the second insert. They are still
/// two separate lines on the order regardless of whether their fields
/// match, which is exactly what reference equality says and value equality
/// does not.
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
/// Milestone 67: a coupon that has already been looked up and found
/// eligible, carrying the percentage it is worth.
///
/// Passing this in resolved - rather than a bare code the rules would have
/// to look up - is what keeps the engine pure now that coupons live in the
/// database instead of configuration. A rule reaching for a repository
/// mid-evaluation would make pricing non-deterministic, and the ten
/// property-based tests depend on it not being.
/// </summary>
public sealed record ResolvedCoupon(string Code, string Description, decimal Percentage);

/// <summary>
/// Everything the rules are allowed to reason about. Deliberately a
/// self-contained snapshot rather than a set of service handles: a rule
/// that could call out to the database mid-evaluation would make pricing
/// non-deterministic and untestable, which is the whole reason the
/// property-based tests can assert invariants over arbitrary inputs.
/// </summary>
/// <summary>
/// Milestone 71: who is buying and where it is going, resolved before
/// pricing for exactly the reason the coupon is - the rules must stay a
/// pure function of the facts handed to them.
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
/// One discount a rule decided to grant. Kept as a list rather than
/// collapsed into a single number so the customer (and support) can see
/// <em>why</em> the total came down - "10% coupon SAVE10" and "5% off
/// electronics" are separate lines on the receipt, not one opaque figure.
///
/// Equality is identity, for the same reason as PricingLine: this is
/// inserted into NRules working memory by a rule's action, and two
/// independently-authored rules (or the same rule firing for two different
/// lines) can legitimately produce a Code, Description and Amount that all
/// happen to match - two lines of the same SKU and quantity earn the same
/// bulk-discount Code/Description, and their Amounts can round to the same
/// centavo even when the underlying subtotals differ. Value equality would
/// make the second grant look like a duplicate of the first and NRules
/// would refuse to insert it, silently dropping (or crashing on) a
/// discount the customer actually earned.
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
/// DiscountTotal - see AllocateDiscounts.
/// </summary>
public sealed record PricingBreakdown(
    Money Subtotal,
    IReadOnlyList<AppliedDiscount> Discounts,
    IReadOnlyList<AppliedCharge> Charges,
    Money DiscountTotal,
    Money ShippingTotal,
    Money TaxTotal,
    Money GrandTotal,
    IReadOnlyList<Money> LineDiscounts);

public static class PricingAllocation
{
    /// <summary>
    /// Spreads an order-level discount across the lines that produced it,
    /// proportionally to each line's subtotal.
    ///
    /// The naive version - multiplying each line by the discount percentage
    /// and rounding - is a classic e-commerce defect: three lines sharing a
    /// R$10,00 discount each get R$3,33 and the order silently loses a
    /// centavo, so the line totals no longer add up to the amount actually
    /// charged.
    ///
    /// Milestone 66 fixed that with NodaMoney's Split. Milestone 70 found
    /// Split fixes it incompletely: the shares always sum back correctly,
    /// but an individual share can come out <em>negative</em> (about 1 in
    /// 200k weighted allocations), which here would be a line whose
    /// "discount" raises its price. MoneyAllocation.Allocate keeps the
    /// exact-sum guarantee and adds non-negativity by construction - see
    /// its comment for how, and for why the original measurement missed it.
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
}

/// <summary>
/// The domain's contract for pricing an order. The implementation
/// (NRules-backed) lives in Orders.Application so the domain never takes a
/// dependency on a rules engine - enforced by an architecture fitness
/// function, not just convention.
/// </summary>
public interface IPricingEngine
{
    PricingBreakdown Price(PricingRequest request);
}
