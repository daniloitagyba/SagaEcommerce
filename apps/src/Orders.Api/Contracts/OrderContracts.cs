namespace Orders.Api.Contracts;

/// <summary>Milestone 66: a requested line. Note there is no price field - see CreateOrderRequest.</summary>
public sealed record CreateOrderItemRequest(string? Sku, int Quantity);

/// <summary>
/// Accepts two shapes, deliberately, for the duration of an
/// expand/contract migration:
///
/// - <c>{ customerId, items: [{ sku, quantity }], couponCode? }</c> is the
///   real one. Prices come from the catalog server-side; the client never
///   states what anything costs.
/// - <c>{ customerId, amount, currency }</c> is the Milestone 7 shape,
///   still posted by the k6 load scripts, the smoke tests, the Pact
///   contracts and the README quickstart. Kept working so a pricing
///   regression stays distinguishable from a migration mistake.
///
/// Sending items wins if both are present.
/// </summary>
public sealed record CreateOrderRequest(
    string? CustomerId,
    decimal Amount,
    string? Currency,
    IReadOnlyList<CreateOrderItemRequest>? Items = null,
    string? CouponCode = null,
    /// <summary>Milestone 68: "Card" (authorize now, capture on shipment) or "Pix" (charged outright). Defaults to Pix.</summary>
    string? PaymentMethod = null,
    /// <summary>Milestone 71: destination. Decides shipping zone and tax jurisdiction; omitted falls back to flat shipping.</summary>
    ShippingAddressRequest? ShippingAddress = null);

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
/// Milestone 23: an order's state reconstructed by folding the event store,
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
