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
    // Milestone 66: needed so the orchestrated payment step can run the
    // same customer-history risk rules as the choreographed one, which
    // gets CustomerId straight off OrderCreated. Defaulted rather than
    // required because these are plain-JSON internal messages with no
    // schema registry behind them - a request already in flight during a
    // deploy deserialises with an empty customer and is simply treated as
    // an unknown customer by the risk rules.
    string CustomerId = "",
    // Milestone 68: same reason CustomerId is here - the decision request
    // is issued at step 2 from the saga row, not from OrderCreated, so
    // anything Payments needs has to be carried through explicitly.
    string PaymentMethod = PaymentMethods.Pix,
    // Milestone 73: same carry-through as the two above - the ADDRESS_MISMATCH
    // risk signal needs to know where this order ships, and the orchestrated
    // path builds this request from the saga row rather than from OrderCreated.
    string ShippingPostalPrefix = "");

public sealed record PaymentDecisionReplied(
    Guid OrderId,
    bool Approved,
    string CorrelationId,
    DateTimeOffset DecidedAt);
