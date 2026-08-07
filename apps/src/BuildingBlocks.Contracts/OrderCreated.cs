namespace BuildingBlocks;

/// <summary>
/// Milestone 66: a line on the published event, carrying only what a
/// consumer needs to act on the purchase - category stays in Orders' own
/// table since the consumer that cares (bestseller tracking) looks it up
/// from Catalog anyway.
/// </summary>
public sealed record OrderCreatedLine(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>
/// Schema version 2 (Milestone 66) adds Lines, backward-compatible by
/// construction: the Avro field defaults to an empty array, so v1 and v2
/// consumers can each read the other's messages without failing. Lines
/// defaults to null on the C# side for the same reason - amount-only
/// construction sites that predate this milestone still compile.
/// </summary>
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
    // Milestone 68: which method the shopper chose. Defaults to Pix so a
    // v1/v2 message read against v3 arrives as an instant payment rather
    // than an authorization nobody will ever capture.
    string PaymentMethod = PaymentMethods.Pix,
    // Milestone 73: the delivery address's first two CEP digits, so risk
    // rules can spot an unfamiliar shipping destination - Payments has no
    // business holding a full street address it would only compare for equality.
    string ShippingPostalPrefix = "")
{
    public IReadOnlyList<OrderCreatedLine> LinesOrEmpty => Lines ?? [];

    public bool HasLineItems => Lines is { Count: > 0 };
}

public static class OrderCreatedSchemaVersions
{
    /// <summary>Milestone 19: amount-only.</summary>
    public const int AmountOnly = 1;

    /// <summary>Milestone 66: adds the Lines array.</summary>
    public const int WithLineItems = 2;

    /// <summary>Milestone 68: adds PaymentMethod.</summary>
    public const int WithPaymentMethod = 3;

    /// <summary>Milestone 73: adds ShippingPostalPrefix.</summary>
    public const int WithShippingPrefix = 4;

    /// <summary>Consumers accept any version they know how to read - pinning to one would turn a rolling deploy into an outage.</summary>
    public static bool IsSupported(int schemaVersion) =>
        schemaVersion is AmountOnly or WithLineItems or WithPaymentMethod or WithShippingPrefix;
}
