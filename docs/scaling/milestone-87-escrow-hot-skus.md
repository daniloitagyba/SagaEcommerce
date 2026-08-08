# Milestone 87: Escrow for Hot SKUs

## Scope

Milestone 51 measured, deliberately, what one SKU costs to serialize: reservation correctness depends entirely on Kafka handing a SKU's partition to exactly one consumer at a time (`InventoryReservationMessageProcessor`'s own comment is explicit that this is why no row lock exists), and `WarehouseStock.TryReserve` is a plain read-then-write with no concurrency control of its own because it has never needed any. That is the right design for the ordinary case and the load-bearing ceiling for the one case it isn't: a flash sale on a single SKU has its entire network-wide throughput bounded by one partition, one consumer, one row, regardless of how many warehouses or how much total stock actually exists.

Escrow is the standard answer: split a hot SKU's available quantity into several independently-lockable buckets, so concurrent reservations no longer have to queue behind each other just because they happen to target the same SKU. This milestone builds and proves the allocation algorithm; it does not rewire Kafka partitioning or the database schema to actually deploy it, for reasons laid out below.

## Design

**`StockEscrow` reuses `StockAllocator` wholesale rather than re-deriving its policy.** Splitting one warehouse's stock into buckets is structurally the same problem Milestone 72 already solved at the warehouse level: given a set of candidates each with an available quantity and a priority, prefer one that alone covers the request, and only spread across several when none can. A bucket is a `StockAllocator.Candidate` the same way a warehouse already is - `TryReserve(preferredBucket, quantity)` ranks every bucket by circular distance from the preferred one and hands the whole decision to the existing allocator. No parallel implementation of "prefer one, split only when needed" exists; there is exactly one, used at two different grains.

**Consistent hash routing, not round-robin.** `PreferredBucket(key, bucketCount)` maps a reservation id (or any per-request key) to the same bucket every time via its hash, so a system that retries a reservation reliably contends against the same bucket rather than a different random one each attempt, while still spreading unrelated requests roughly evenly across all buckets over time.

**Immutable and pure, proven rather than asserted.** `Split`, `TryReserve`, `Apply`, and `Release` each return a new `StockEscrow`; nothing is mutated in place. `StockEscrowPropertyTests` checks, against thousands of randomly generated splits and requests: `Split` preserves the total exactly and never lets buckets differ by more than one unit; any reservation within the total is fulfillable from some combination of buckets and draws exactly the requested quantity; any reservation exceeding the total is never fulfillable; and reserving then releasing the same plan always returns the grand total to exactly where it started - the same "never oversell, never leak a unit" guarantee `StockAllocator` itself already carries, now composed at a finer grain.

**The throughput case is measured, the same discipline Milestone 51 used to establish the ceiling this answers.** `StockEscrowConcurrencyTests` runs the same total amount of simulated work - a fixed per-operation delay standing in for a database row's round trip - once behind a single lock (today's one-row-per-SKU shape) and once behind N per-bucket locks, and asserts the bucketed run completes in well under half the time. Neither side touches a real Kafka partition or Postgres row (no live cluster reaches this environment); what's measured is genuinely the *effect of the locking shape itself* - one lock serializes regardless of how much underlying capacity exists behind it, N locks let up to N operations proceed at once - which is the actual mechanism a real deployment would be relying on, not an assumption about it.

## Why this milestone stops at the algorithm

Deploying escrow for real needs two changes this milestone does not make, both correctness-sensitive enough to want a live system to validate against rather than guess at:

1. **The Kafka partition key would have to become `{sku}-{bucket}`, not `{sku}`.** Milestone 41's whole safety argument is that a SKU's reservations are handled by exactly one consumer at a time; splitting a SKU across buckets only buys real parallelism if *different buckets* of the same SKU can land on *different* partitions/consumers concurrently - which means the producer side (`OrderSagaOrchestrator`'s publish, `SagaTimeoutSweeper`'s release) needs to know which bucket a reservation targets before it's produced, and the consumer side needs a topic with enough partitions to actually spread bucket traffic out.
2. **The shared aggregate row (`InventoryItem.AvailableQuantity`/`ReservedQuantity`) stops being safe to touch with a plain read-then-write the moment two buckets of the same SKU can be processed concurrently.** `WarehouseAllocationStore`'s own class comment says this precisely: "no row locking needed... two requests for the same SKU are never processed concurrently." Escrow deployed for real breaks that premise for exactly the SKU it's meant to help, and the aggregate row would need either a genuine atomic `UPDATE ... WHERE` guard or its own decomposition into per-bucket rows before concurrent bucket processing would be safe rather than merely fast.

Both are real, bounded pieces of work - not vague future scope - and both need a running Kafka cluster and a running Postgres to validate the exact failure mode they're supposed to prevent, which this environment does not have. Building the algorithm and proving its properties and its throughput shape first, in isolation, is what makes that follow-up milestone tractable rather than speculative.

## Verification performed

Same constraint as every milestone in this pass: no Docker, no live Kafka or Postgres reachable here.

- **Full solution build**: 0 warnings, 0 errors.
- **`StockEscrowPropertyTests`** (7 facts, `Inventory.IntegrationTests` - pure logic): split-preserves-total and no-bucket-differs-by-more-than-one (2,000 random samples each), a fulfillable reservation within the total always succeeds and reserves exactly what was asked (5,000 samples), an unfulfillable one never does (2,000 samples), reserve-then-release round-trips the grand total exactly (5,000 samples), and a full sequential drain across every bucket via `PreferredBucket` exhausts the network to precisely zero with nothing lost.
- **`StockEscrowConcurrencyTests`** (1 fact): bucketed locking completes the same simulated workload in under half the time a single lock takes, run three times in this session with no flake.
- **Not verified in this pass**: any real Kafka partition, any real Postgres row, `WarehouseAllocationStore` actually using `StockEscrow` at all (it does not yet - see above), and therefore the exact concurrency-safety question the aggregate row would raise once it did.

## What was deliberately not done

- **Wiring `StockEscrow` into `WarehouseAllocationStore`, the Kafka partition key, or a new `warehouse_stock_buckets`-shaped migration.** See "Why this milestone stops at the algorithm."
- **Deciding a real bucket count per SKU.** A fixed `bucketCount` was used throughout testing; a real deployment would need a policy for which SKUs are hot enough to warrant splitting at all; a single-bucket "escrow" degenerates to exactly today's behavior, so the mechanism is safe to leave off for every SKU that doesn't need it.
- **Rebalancing bucket boundaries after a restock.** `Split` always starts from an even distribution; a hot SKU that sells unevenly across buckets over time (some drained, some still full) would eventually want a live rebalance, not just a fresh even split at restock time - a real refinement, not built here.

## See also

- [Milestone 51: Rebalance vs the Per-SKU Serialization Guarantee](../architecture/milestone-51-rebalance-vs-sku-serialization.md) — the measured single-partition ceiling this milestone's algorithm answers.
- [Milestone 72: Stock Lives in Buildings](../domain/milestone-72-multi-warehouse-allocation.md) — `StockAllocator`, reused wholesale rather than re-derived for buckets.
- [Milestone 41: Inventory Service with Kafka-Partitioned Stock Reservation](../architecture/milestone-41-inventory-kafka-partitioning.md) — the per-SKU partition guarantee `WarehouseAllocationStore`'s lock-free design depends on, and the exact premise a live deployment of escrow would have to renegotiate.
