namespace BuildingBlocks;

/// <summary>A line on the published event, carrying only what a consumer needs to act on the purchase.</summary>
public sealed record OrderCreatedLine(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>Schema version 2 adds Lines, backward-compatible by construction via a defaulted empty array.</summary>
public sealed record OrderCreated(
    Guid EventId,
    Guid OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    IReadOnlyList<OrderCreatedLine>? Lines = null,
    int SchemaVersion = OrderCreatedSchemaVersions.WithShippingPrefix,
    string PaymentMethod = PaymentMethods.Pix,
    string ShippingPostalPrefix = "")
{
    public IReadOnlyList<OrderCreatedLine> LinesOrEmpty => Lines ?? [];

    public bool HasLineItems => Lines is { Count: > 0 };
}

public static class OrderCreatedSchemaVersions
{
    /// <summary>Amount-only.</summary>
    public const int AmountOnly = 1;

    /// <summary>Adds the Lines array.</summary>
    public const int WithLineItems = 2;

    /// <summary>Adds PaymentMethod.</summary>
    public const int WithPaymentMethod = 3;

    /// <summary>Adds ShippingPostalPrefix.</summary>
    public const int WithShippingPrefix = 4;

    /// <summary>Consumers accept any version they know how to read.</summary>
    public static bool IsSupported(int schemaVersion) =>
        schemaVersion is AmountOnly or WithLineItems or WithPaymentMethod or WithShippingPrefix;
}
