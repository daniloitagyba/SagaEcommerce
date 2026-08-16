namespace Orders.Worker;

/// <summary>
/// How often, and how much, the anti-entropy sweep looks at
/// per tick. An audit loop, not a hot path - the interval defaults far
/// longer than any other sweeper in this codebase (SagaTimeoutSweeper,
/// BackorderTimeoutSweeper), since it exists to catch a class of bug that
/// should be rare by construction, not to react to one within seconds.
/// </summary>
public sealed class AntiEntropyOptions
{
    public const string SectionName = "AntiEntropy";

    public int SweepIntervalSeconds { get; init; } = 300;

    /// <summary>
    /// How many candidate rows each check examines per tick. Bounded per
    /// tick, but no longer bounded overall: as of Milestone 91, the two
    /// Orders-database-local checks (payment-accounted, write/read-model)
    /// walk the table via a durable cursor (anti_entropy_progress) that
    /// advances every tick and wraps back to the start once it reaches the
    /// end, instead of always re-examining the newest BatchSize rows -
    /// which, before this, meant a store with more than BatchSize eligible
    /// orders per sweep interval could never have its older rows checked
    /// at all. See docs/roadmap-milestones-91-99.md, "the anti-entropy
    /// sweep can only ever see the newest rows".
    /// </summary>
    public int BatchSize { get; init; } = 200;

    public string PaymentsBaseUrl { get; init; } = "http://payments-service:8080";

    public string InventoryBaseUrl { get; init; } = "http://inventory-service:8080";

    /// <summary>
    /// How long order_summaries' own last projection has to have gone
    /// stale before a status mismatch against orders counts as a
    /// divergence, not just ordinary in-flight outbox/Kafka lag - see
    /// AntiEntropyChecks.WriteModelDivergesFromReadModel. Comfortably
    /// longer than the outbox's own poll interval plus a Kafka round trip.
    /// </summary>
    public int ProjectionLagThresholdSeconds { get; init; } = 120;

    /// <summary>
    /// How old a Created or Backordered order has to be, with no
    /// saga_orchestration_states row at all, before CheckOrdersStuckWithoutASagaRowAsync
    /// counts it as a divergence rather than a saga simply not having
    /// started or finished yet. Comfortably longer than
    /// SagaOrchestration:TimeoutSeconds (the saga's own timeout, 90s by
    /// default as of Milestone 91) - this check exists for the row that
    /// timeout can no longer resolve because it is already gone, not to
    /// duplicate the timeout itself.
    /// </summary>
    public int StuckOrderThresholdSeconds { get; init; } = 600;
}
