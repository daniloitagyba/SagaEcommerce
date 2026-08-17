namespace BuildingBlocks;

/// <summary>The orchestrated saga's command/reply contracts.</summary>
public sealed record PaymentDecisionRequested(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string CorrelationId,
    DateTimeOffset RequestedAt,
    string CustomerId = "",
    string PaymentMethod = PaymentMethods.Pix,
    string ShippingPostalPrefix = "");

public sealed record PaymentDecisionReplied(
    Guid OrderId,
    bool Approved,
    string CorrelationId,
    DateTimeOffset DecidedAt);
