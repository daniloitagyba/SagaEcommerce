using Microsoft.Extensions.Options;
using NodaMoney;
using NRules;
using NRules.Fluent;
using Orders.Domain.Pricing;

namespace Orders.Application.Pricing;

/// <summary>
/// Milestone 66: prices an order by running the promotion rules, then
/// assembling the result deterministically.
///
/// The split is deliberate. Promotions are what a rules engine is actually
/// good at: independent, individually-authored, freely-combining rules
/// whose interactions are the hard part - which is why they live in
/// PromotionRules.cs as declarative rules. Shipping and tax are not
/// promotions, they are arithmetic policy applied in a fixed order
/// (tax on the discounted subtotal, never the gross one), so encoding
/// their ordering as rule priorities would add fragility to buy nothing.
/// Free shipping is the one genuine promotion among them and it <em>is</em>
/// a rule - it just grants a marker fact this method reads.
///
/// The engine also owns the one invariant no individual rule can enforce:
/// discounts are capped at the subtotal. Two rules each granting 60% is a
/// perfectly reasonable thing for two independently-authored campaigns to
/// do, and without the cap the order total goes negative - the single most
/// valuable thing the property-based tests pin down.
/// </summary>
public sealed class NRulesPricingEngine : IPricingEngine
{
    private readonly ISessionFactory _sessionFactory;
    private readonly PricingOptions _options;

    public NRulesPricingEngine(IOptions<PricingOptions> options)
    {
        _options = options.Value;

        var repository = new RuleRepository();
        repository.Load(source => source.From(typeof(CouponPercentageRule).Assembly));
        // Compiling the Rete network is expensive and the result is
        // immutable and thread-safe, so it happens once per process here
        // and every Price call gets a cheap session off it.
        _sessionFactory = repository.Compile();
    }

    public PricingBreakdown Price(PricingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = request.Currency;
        var zero = new Money(0m, currency);
        var subtotal = request.Lines.Count == 0 ? zero : request.Subtotal;

        var session = _sessionFactory.CreateSession();
        session.Insert(request);
        session.Insert(new PromotionPolicy(_options));
        foreach (var line in request.Lines)
        {
            session.Insert(line);
        }

        session.Fire();

        var discounts = session.Query<AppliedDiscount>()
            .Where(discount => discount.Amount > zero)
            .OrderBy(discount => discount.Code, StringComparer.Ordinal)
            .ToList();
        var freeShipping = session.Query<FreeShippingGranted>().Any();

        var rawDiscountTotal = discounts.Aggregate(zero, (running, discount) => running + discount.Amount);
        var discountTotal = rawDiscountTotal > subtotal ? subtotal : rawDiscountTotal;

        if (rawDiscountTotal > subtotal)
        {
            // Keep the receipt honest: the itemised discounts must still
            // add up to the total that was actually applied, so the excess
            // is absorbed by shrinking the last one rather than left to
            // contradict the summary.
            discounts = CapDiscounts(discounts, subtotal, currency);
        }

        var discountedSubtotal = subtotal - discountTotal;

        var charges = new List<AppliedCharge>();
        var shippingTotal = zero;
        var shippingAmount = ResolveShipping(request);
        if (request.Lines.Count > 0 && !freeShipping && shippingAmount > 0m)
        {
            shippingTotal = new Money(shippingAmount, currency);
            charges.Add(new AppliedCharge("SHIPPING", DescribeShipping(request), shippingTotal));
        }

        var taxTotal = zero;
        var taxRate = ResolveTaxRate(request);
        if (taxRate > 0m)
        {
            taxTotal = new Money(discountedSubtotal.Amount * taxRate / 100m, currency);
            charges.Add(new AppliedCharge("TAX", $"Tax at {taxRate:0.##}%", taxTotal));
        }

        var grandTotal = discountedSubtotal + shippingTotal + taxTotal;
        var lineDiscounts = PricingAllocation.AllocateDiscounts(discountTotal, request.Lines, currency);

        return new PricingBreakdown(
            subtotal,
            discounts,
            charges,
            discountTotal,
            shippingTotal,
            taxTotal,
            grandTotal,
            lineDiscounts);
    }

    /// <summary>
    /// Milestone 71: shipping follows the destination.
    ///
    /// Falls back to the flat rate when no address was supplied at all -
    /// the amount-only checkout shape has no destination, and refusing to
    /// price it would break the expand/contract promise Milestone 66 made.
    /// </summary>
    private decimal ResolveShipping(PricingRequest request)
    {
        if (request.Destination is not { } destination || destination.PostalPrefix.Length == 0)
        {
            return _options.FlatShippingAmount;
        }

        return _options.ShippingByPostalPrefix.TryGetValue(destination.PostalPrefix, out var cost)
            ? cost
            : _options.DefaultShippingAmount;
    }

    private static string DescribeShipping(PricingRequest request) =>
        request.Destination is { } destination && destination.PostalPrefix.Length > 0
            ? $"Shipping to {destination.Region} ({destination.PostalPrefix})"
            : "Standard shipping";

    /// <summary>
    /// Tax follows where the goods land, not where the shop is. With no
    /// destination the global rate applies - which defaults to 0, exactly
    /// as it did before addresses existed.
    /// </summary>
    private decimal ResolveTaxRate(PricingRequest request)
    {
        if (request.Destination is { } destination
            && _options.TaxRateByRegion.TryGetValue(destination.Region, out var regional))
        {
            return regional;
        }

        return _options.TaxRatePercentage;
    }

    private static List<AppliedDiscount> CapDiscounts(
        List<AppliedDiscount> discounts,
        Money subtotal,
        Currency currency)
    {
        var capped = new List<AppliedDiscount>(discounts.Count);
        var remaining = subtotal;
        var zero = new Money(0m, currency);

        foreach (var discount in discounts)
        {
            if (remaining <= zero)
            {
                break;
            }

            var amount = discount.Amount > remaining ? remaining : discount.Amount;
            capped.Add(discount with { Amount = amount });
            remaining -= amount;
        }

        return capped;
    }
}
