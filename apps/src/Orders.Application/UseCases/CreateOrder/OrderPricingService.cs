using BuildingBlocks;
using NodaMoney;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Domain.Pricing;

namespace Orders.Application.UseCases.CreateOrder;

public sealed record PricedCheckout(
    string Currency,
    IReadOnlyList<OrderLineDraft> Lines,
    PricingBreakdown Breakdown,
    /// <summary>Set only when a coupon was resolved and found eligible - the checkout must then claim a redemption slot for it.</summary>
    string? CouponCode);

/// <summary>
/// Milestone 66: turns "SKU + quantity" into a priced order. The catalog
/// lookup happens here, server-side - a client posting a SKU and quantity
/// gets today's catalog price regardless of what it thinks the price is,
/// revalidating whatever Cart.Service snapshotted when the item was added.
/// </summary>
public sealed class OrderPricingService(
    ICatalogClient catalogClient,
    ICouponRepository couponRepository,
    ICustomerRepository customerRepository,
    IPricingEngine pricingEngine,
    TimeProvider timeProvider)
{
    public async Task<(PricedCheckout? Checkout, Dictionary<string, string[]> Errors)> PriceAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var items = command.Items!;
        var snapshots = new List<(CreateOrderItem Item, CatalogProductSnapshot Product)>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            CatalogProductSnapshot? product;

            try
            {
                product = await catalogClient.FindBySkuAsync(item.Sku!, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Without a price there is no order - surfaced as an infrastructure fault (503), not a validation error, since retrying is correct here.
                throw new InfrastructureUnavailableException(
                    "Catalog.Service is currently unavailable, so the order cannot be priced.",
                    exception);
            }

            if (product is null)
            {
                errors[$"Items[{index}].Sku"] = [$"SKU '{item.Sku}' was not found in the catalog."];
                continue;
            }

            snapshots.Add((item, product));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var currencyCodes = snapshots
            .Select(entry => entry.Product.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currencyCodes.Count > 1)
        {
            // A multi-currency order has no single total to charge - reject rather than invent an exchange rate.
            errors["Items"] = [$"All items must share one currency, but the catalog returned {string.Join(", ", currencyCodes)}."];
            return (null, errors);
        }

        var currencyCode = currencyCodes[0];
        Currency currency;
        try
        {
            currency = Currency.FromCode(currencyCode);
        }
        catch (ArgumentException)
        {
            errors["Items"] = [$"The catalog returned an unknown currency '{currencyCode}'."];
            return (null, errors);
        }

        var pricingLines = snapshots
            .Select(entry => new PricingLine(
                entry.Product.Sku,
                entry.Product.Name,
                entry.Product.CategorySlug,
                entry.Item.Quantity,
                new Money(entry.Product.Price, currency)))
            .ToList();

        // Milestone 67: resolve the coupon before pricing, never during - keeps the rules engine free of I/O.
        ResolvedCoupon? resolvedCoupon = null;
        if (!string.IsNullOrWhiteSpace(command.CouponCode))
        {
            var subtotal = pricingLines.Aggregate(
                new Money(0m, currency),
                (running, line) => running + line.LineSubtotal);

            var (rejection, coupon) = await ResolveCouponAsync(
                command.CouponCode, command.CustomerId!, subtotal.Amount, cancellationToken);

            if (rejection != CouponRejectionReason.None)
            {
                // A bad coupon fails the checkout instead of being silently dropped - now that coupons expire and run out, the shopper deserves to know why.
                errors[nameof(CreateOrderCommand.CouponCode)] =
                    [CouponEligibility.Describe(rejection, command.CouponCode.Trim().ToUpperInvariant())];
                return (null, errors);
            }

            resolvedCoupon = coupon;
        }

        // Milestone 71: customer standing and destination resolved here too, so the rules stay pure functions of facts, never a repository lookup.
        var customer = await customerRepository.GetOrCreateAsync(command.CustomerId!, cancellationToken);
        var destination = command.ShippingAddress is { IsComplete: true } address
            ? new PricingDestination(address.Region, address.PostalPrefix)
            : null;

        var breakdown = pricingEngine.Price(new PricingRequest(
            command.CustomerId!,
            currency,
            pricingLines,
            resolvedCoupon,
            new PricingCustomer(customer.Id, customer.Tier, customer.CreatedAt),
            destination));

        var drafts = pricingLines
            .Select((line, index) => new OrderLineDraft(
                line.Sku,
                line.ProductName,
                line.CategorySlug,
                line.Quantity,
                line.UnitPrice.Amount,
                breakdown.LineDiscounts[index].Amount))
            .ToList();

        return (new PricedCheckout(currencyCode, drafts, breakdown, resolvedCoupon?.Code), errors);
    }

    private async Task<(CouponRejectionReason Rejection, ResolvedCoupon? Coupon)> ResolveCouponAsync(
        string couponCode,
        string customerId,
        decimal subtotal,
        CancellationToken cancellationToken)
    {
        var (snapshot, customerRedemptions) = await couponRepository.FindAsync(couponCode, customerId, cancellationToken);
        var rejection = CouponEligibility.Evaluate(snapshot, subtotal, customerRedemptions, timeProvider.GetUtcNow());

        return rejection == CouponRejectionReason.None
            ? (rejection, new ResolvedCoupon(snapshot!.Code, snapshot.Description, snapshot.Percentage))
            : (rejection, null);
    }
}
