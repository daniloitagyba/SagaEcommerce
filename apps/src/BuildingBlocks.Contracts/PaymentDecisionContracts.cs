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
    DateTimeOffset RequestedAt);

public sealed record PaymentDecisionReplied(
    Guid OrderId,
    bool Approved,
    string CorrelationId,
    DateTimeOffset DecidedAt);
