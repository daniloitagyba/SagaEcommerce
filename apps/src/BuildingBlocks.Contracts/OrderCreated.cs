namespace BuildingBlocks;

/// <summary>
/// Milestone 66: a line on the published event. Carries what a consumer
/// needs to act on the purchase (which SKU, how many, what it cost) and
/// nothing it does not - the category, for instance, stays behind in
/// Orders' own table because the consumers that care about it
/// (bestseller tracking) already look it up from the Catalog.
/// </summary>
public sealed record OrderCreatedLine(
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>
/// Schema version 2 (Milestone 66) adds Lines. The change is
/// backward-compatible by construction: the Avro field carries a default
/// of an empty array, so a consumer still running the v1 schema reads a
/// v2 message without failing, and a v1 message read against the v2
/// schema simply arrives with no lines - which is exactly what an
/// amount-only order means. Lines defaults to null here for the same
/// reason on the C# side: the amount-only <see cref="OrderCreated"/>
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
    // Milestone 68: which method the shopper chose, since it decides
    // whether Payments places a hold to capture later or charges outright.
    // Defaulted for the same reason Lines is - so the amount-only
    // construction sites that predate it still compile, and so a v1/v2
    // message read against v3 arrives as an instant Pix rather than an
    // authorization nobody will ever capture.
    string PaymentMethod = PaymentMethods.Pix,
    // Milestone 73: the first two CEP digits of the delivery address, so
    // the payment risk rules can tell "shipping where this customer always
    // ships" from "shipping somewhere they never have". The prefix rather
    // than the address because it is the coarsest thing that still answers
    // the question - Payments has no business holding a street address it
    // would only ever compare for equality.
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

    /// <summary>
    /// Consumers accept any version they know how to read, rather than
    /// pinning to one. Rejecting "not exactly the newest" is what turns a
    /// backward-compatible schema change into a rolling-deploy outage:
    /// during the rollout both versions are genuinely in flight.
    /// </summary>
    public static bool IsSupported(int schemaVersion) =>
        schemaVersion is AmountOnly or WithLineItems or WithPaymentMethod or WithShippingPrefix;
}
