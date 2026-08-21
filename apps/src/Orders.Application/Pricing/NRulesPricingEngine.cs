using Microsoft.Extensions.Options;
using NodaMoney;
using NRules;
using NRules.Fluent;
using Orders.Domain.Pricing;

namespace Orders.Application.Pricing;

public sealed class NRulesPricingEngine : IPricingEngine
{
    private readonly ISessionFactory _sessionFactory;
    private readonly PricingOptions _options;

    public NRulesPricingEngine(IOptions<PricingOptions> options)
    {
        _options = options.Value;

        var repository = new RuleRepository();
        repository.Load(source => source.From(typeof(CouponPercentageRule).Assembly));
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
        discounts = ResolveExclusivity(discounts);
        var freeShipping = session.Query<FreeShippingGranted>().Any();

        var rawDiscountTotal = discounts.Aggregate(zero, (running, discount) => running + discount.Amount);
        var discountTotal = rawDiscountTotal > subtotal ? subtotal : rawDiscountTotal;

        if (rawDiscountTotal > subtotal)
        {
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
        var lineTaxes = PricingAllocation.AllocateTax(taxTotal, request.Lines, lineDiscounts, currency);

        return new PricingBreakdown(
            subtotal,
            discounts,
            charges,
            discountTotal,
            shippingTotal,
            taxTotal,
            grandTotal,
            lineDiscounts,
            lineTaxes);
    }

    /// <summary>Shipping follows the destination, falling back to the flat rate when the amount-only checkout shape supplies no address.</summary>
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

    /// <summary>Tax follows where the goods land, not the shop; no destination means the global rate (0 by default).</summary>
    private decimal ResolveTaxRate(PricingRequest request)
    {
        if (request.Destination is { } destination
            && _options.TaxRateByRegion.TryGetValue(destination.Region, out var regional))
        {
            return regional;
        }

        return _options.TaxRatePercentage;
    }

    private static List<AppliedDiscount> ResolveExclusivity(List<AppliedDiscount> discounts)
    {
        var ungrouped = discounts.Where(discount => discount.ExclusivityGroup is null);
        var winners = discounts
            .Where(discount => discount.ExclusivityGroup is not null)
            .GroupBy(discount => discount.ExclusivityGroup, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(discount => discount.Amount)
                .ThenBy(discount => discount.Code, StringComparer.Ordinal)
                .First());

        return [.. ungrouped.Concat(winners).OrderBy(discount => discount.Code, StringComparer.Ordinal)];
    }

    private static List<AppliedDiscount> CapDiscounts(
        List<AppliedDiscount> discounts,
        Money subtotal,
        Currency currency)
    {
        var capped = new List<AppliedDiscount>(discounts.Count);
        var remaining = subtotal;
        var zero = new Money(0m, currency);

        var payoutOrder = discounts
            .OrderBy(discount => DiscountPriority.RankOf(discount.Code))
            .ThenBy(discount => discount.Code, StringComparer.Ordinal);

        foreach (var discount in payoutOrder)
        {
            if (remaining <= zero)
            {
                break;
            }

            var amount = discount.Amount > remaining ? remaining : discount.Amount;
            capped.Add(discount with { Amount = amount });
            remaining -= amount;
        }

        return [.. capped.OrderBy(discount => discount.Code, StringComparer.Ordinal)];
    }
}

internal static class DiscountPriority
{
    private static readonly string[] AutomaticPrefixes = ["CATEGORY-", "BULK-", "TIER-"];

    public static int RankOf(string code) =>
        Array.Exists(AutomaticPrefixes, prefix => code.StartsWith(prefix, StringComparison.Ordinal)) ? 1 : 0;
}
