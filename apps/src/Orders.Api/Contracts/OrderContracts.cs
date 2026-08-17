namespace Orders.Api.Contracts;

/// <summary>A requested order line; price is resolved server-side, not supplied by the caller.</summary>
public sealed record CreateOrderItemRequest(string? Sku, int Quantity);

/// <summary>Creates an order from requested items.</summary>
public sealed record CreateOrderRequest(
    string? CustomerId,
    IReadOnlyList<CreateOrderItemRequest>? Items,
    string? CouponCode = null,
    /// <summary>"Card" or "Pix"; defaults to Pix.</summary>
    string? PaymentMethod = null,
    /// <summary>Delivery destination, used to decide shipping zone and tax jurisdiction.</summary>
    ShippingAddressRequest? ShippingAddress = null,
    /// <summary>The subtotal the caller's cart last saw; a mismatch with the live catalog returns 409.</summary>
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

/// <summary>Represents order pricing.</summary>
public sealed record OrderPricingResponse(
    decimal Subtotal,
    decimal DiscountTotal,
    decimal ShippingTotal,
    decimal TaxTotal,
    string? CouponCode,
    IReadOnlyList<OrderLineResponse> Lines,
    string? CampaignCode = null);

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

/// <summary>The CQRS read-model projection, built asynchronously by the Orders.Worker projector.</summary>
public sealed record OrderSummaryResponse(
    Guid OrderId,
    string? CustomerId,
    decimal? Amount,
    string? Currency,
    string Status,
    DateTimeOffset? OrderCreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset ProjectedAt);

/// <summary>An order's state reconstructed from the event store, alongside the raw events that produced it.</summary>
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
