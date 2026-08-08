namespace Orders.Api.Contracts;

/// <summary>A requested line. Note there is no price field - see CreateOrderRequest.</summary>
public sealed record CreateOrderItemRequest(string? Sku, int Quantity);

/// <summary>
/// Accepts two shapes for the duration of an expand/contract migration:
/// <c>{ customerId, items, couponCode? }</c>, the real one, prices from the
/// catalog server-side; and the original <c>{ customerId, amount,
/// currency }</c> shape, still posted by k6, smoke tests, Pact and the
/// README quickstart. Items wins if both are present.
/// </summary>
public sealed record CreateOrderRequest(
    string? CustomerId,
    decimal Amount,
    string? Currency,
    IReadOnlyList<CreateOrderItemRequest>? Items = null,
    string? CouponCode = null,
    /// <summary>"Card" (authorize now, capture on shipment) or "Pix" (charged outright). Defaults to Pix.</summary>
    string? PaymentMethod = null,
    /// <summary>Destination. Decides shipping zone and tax jurisdiction; omitted falls back to flat shipping.</summary>
    ShippingAddressRequest? ShippingAddress = null,
    /// <summary>What the caller's cart last saw the subtotal as. Omitted skips the check; present and disagreeing with the live catalog returns 409, not a silent recharge.</summary>
    decimal? ExpectedSubtotal = null);

public sealed record ShippingAddressRequest(string? Line1, string? City, string? Region, string? PostalCode);

public sealed record OrderLineResponse(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineSubtotal,
    decimal LineDiscount,
    decimal LineTotal);

/// <summary>
/// Why the total is what it is. Null on an amount-only order, which has no
/// breakdown to report rather than an empty one.
/// </summary>
public sealed record OrderPricingResponse(
    decimal Subtotal,
    decimal DiscountTotal,
    decimal ShippingTotal,
    decimal TaxTotal,
    string? CouponCode,
    IReadOnlyList<OrderLineResponse> Lines);

public sealed record OrderResponse(
    Guid Id,
    string CustomerId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    string CorrelationId,
    string InstanceId,
    OrderPricingResponse? Pricing = null,
    string? PaymentMethod = null);

/// <summary>
/// Read-model projection (CQRS query side). Built asynchronously by the
/// projector in Orders.Worker; fields can be null for a short window if this
/// row was seeded by a PaymentDecided event that arrived before the
/// corresponding OrderCreated event was projected.
/// </summary>
public sealed record OrderSummaryResponse(
    Guid OrderId,
    string? CustomerId,
    decimal? Amount,
    string? Currency,
    string Status,
    DateTimeOffset? OrderCreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset ProjectedAt);

/// <summary>
/// An order's state reconstructed by folding the event store,
/// alongside the raw events that produced it - the audit trail.
/// </summary>
public sealed record OrderHistoryResponse(
    Guid OrderId,
    OrderSnapshotResponse? Snapshot,
    IReadOnlyList<OrderEventResponse> Events);

public sealed record OrderSnapshotResponse(
    string? CustomerId,
    decimal? Amount,
    string? Currency,
    string Status,
    DateTimeOffset? CreatedAt);

public sealed record OrderEventResponse(
    long Id,
    string EventType,
    DateTimeOffset OccurredAt);
