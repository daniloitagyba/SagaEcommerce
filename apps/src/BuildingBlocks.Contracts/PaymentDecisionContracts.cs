namespace BuildingBlocks;

/// <summary>
/// Milestone 22: the orchestrated saga's command/reply contracts. Plain JSON,
/// not registered with the schema registry - these are internal,
/// transient request/reply messages between one orchestrator and one
/// responder, not a domain event other consumers will ever need to evolve
/// against independently.
/// </summary>
public sealed record PaymentDecisionRequested(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string CorrelationId,
    DateTimeOffset RequestedAt,
    // Milestone 66: lets the orchestrated payment step run the same
    // customer-history risk rules as the choreographed one, which gets
    // CustomerId straight off OrderCreated. Defaulted, not required, since
    // an in-flight request during a deploy deserialises with an empty
    // customer and is simply treated as unknown.
    string CustomerId = "",
    // Milestone 68/73: same carry-through as CustomerId - issued at step 2
    // from the saga row, not OrderCreated, so Payments needs it explicitly.
    string PaymentMethod = PaymentMethods.Pix,
    string ShippingPostalPrefix = "");

public sealed record PaymentDecisionReplied(
    Guid OrderId,
    bool Approved,
    string CorrelationId,
    DateTimeOffset DecidedAt);
