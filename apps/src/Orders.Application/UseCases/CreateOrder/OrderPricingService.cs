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
/// Milestone 66: turns "SKU + quantity" into a priced order.
///
/// The catalog lookup happens here, server-side, and the request's own
/// notion of price is never consulted - a client that posts
/// {"sku":"SKU-ELEC-001","quantity":1} gets today's catalog price whether
/// it likes it or not. Cart.Service deliberately snapshots the price the
/// shopper saw when they added an item (see CartLineItem), which is the
/// right behaviour for a cart and the wrong one for a charge; checkout is
/// where that snapshot gets revalidated against reality, exactly as
/// CartLineItem's own comment promised but nothing implemented until now.
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
                // Without a price there is no order. Surfacing this as an
                // infrastructure fault (503 + Retry-After) rather than a
                // validation error matters: the request was perfectly
                // valid and retrying it is the correct client behaviour.
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
            // A multi-currency order has no single total to charge. Better
            // to reject it than to invent an exchange rate this lab has no
            // business owning.
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

        // Milestone 67: resolve the coupon before pricing, never during.
        // The subtotal needed for the minimum-order check is just the sum
        // of the lines, which does not depend on the coupon - so this
        // ordering costs nothing and keeps the rules engine free of I/O.
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
                // A bad coupon fails the checkout instead of being silently
                // dropped. Milestone 66 ignored unknown codes because a
                // config typo was the only way to get one; now that coupons
                // expire and run out, "why is this not applying?" is a
                // question the shopper deserves an answer to.
                errors[nameof(CreateOrderCommand.CouponCode)] =
                    [CouponEligibility.Describe(rejection, command.CouponCode.Trim().ToUpperInvariant())];
                return (null, errors);
            }

            resolvedCoupon = coupon;
        }

        // Milestone 71: the customer's standing and the destination are
        // resolved here for the same reason the coupon is - the rules stay
        // a pure function of facts handed to them, never of a repository
        // they could reach for mid-evaluation.
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
