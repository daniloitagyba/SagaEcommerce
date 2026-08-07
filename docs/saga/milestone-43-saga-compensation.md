# Milestone 43: Extending the Orchestrated Saga to 4 Steps with Compensation

## Scope

The last Tier A gap-analysis item, and the payoff for M41's Inventory Service: the orchestrated saga from Milestone 22 only ever had one step (request a payment decision). This milestone extends it to a genuine 4-step saga - **Reserve Inventory → Decide Payment → Commit Inventory (approved) | Release Inventory (declined)** - with a real compensating transaction, not just a description of one.

## Why this is the orchestrated saga, not the choreographed one

This lab has run two saga implementations side by side since M22 specifically to compare them: a choreographed one (`Payments.Service` autonomously reacting to `OrderCreated`) and an orchestrated one (`OrderSagaOrchestrator`/`OrderSagaReplyConsumer`, an explicit state machine). Compensation is the orchestrated style's natural home - something has to *know* the saga failed at step N and decide what to undo at step N-1, and that's exactly what an explicit orchestrator is for. The choreographed path stays completely untouched by this milestone, still converging correctly on its own.

## Design

- **`InventoryItem` gains `TryCommit`/`TryRelease`** (`Inventory.Service/Domain/InventoryItem.cs`), the two ways a reservation gets settled: `TryCommit` turns a temporary hold into a permanent deduction (`ReservedQuantity -= qty`, `AvailableQuantity` never returns); `TryRelease` is the compensation itself (`ReservedQuantity -= qty`, `AvailableQuantity += qty`) - the reservation was never the problem, so it gets undone.
- **Four new Kafka topics** (`inventory.reservation-commit-requested/replied.v1`, `inventory.reservation-release-requested/replied.v1`), each produced keyed by `Sku` for the same partition-ownership reason as the original M41 reservation request. Commit/Release reuse the **original `ReservationId`** from the M41 Reserve request rather than minting a new one - they act ON that reservation, not a new independent operation.
- **Inbox dedup uses a per-operation `consumer_name` suffix** (`inventory-service-commit`, `inventory-service-release`) rather than the plain consumer group name. Since Commit/Release deliberately reuse the Reserve's `ReservationId`, using the same inbox dedup key as Reserve would make the Commit/Release message look like a duplicate of the *original* Reserve and get silently skipped. A real bug caught by writing the test that exercises exactly this reuse (`CommitAndReleaseReuseTheSameReservationIdWithoutInboxCollision`) before it could ever reach the live cluster.
- **`SagaOrchestrationState` (M36) grows from "one pending request" into "one pending step of a multi-step saga"**: `Step`, `ReservationId`, `Sku`, `Quantity`, `Amount`, `Currency`. At most one reply is ever outstanding per order at a time (the steps are strictly sequential), so this stays one row per order - `Step` just says which reply is currently expected. Every transition is `UPDATE ... WHERE step = @expected ... RETURNING`, so a stale or duplicate reply for a step the saga has already moved past is a no-op instead of corrupting a later step's state - the same defensive pattern M41's Inventory processor already uses for its own inbox.
- **`OrderSagaReplyConsumer` now subscribes to all four reply topics** (inventory reserve, payment decision, inventory commit, inventory release) in one consumer and drives every transition as replies arrive. One consumer rather than four separate classes because only one reply is ever outstanding per order - there's no concurrent-step case to reason about.
- **`Order` (M7) stays amount-only and untouched on purpose.** *(Superseded by Milestone 66, which added real line items and deleted `SagaSkuMapper` - the saga now reserves the SKU the customer actually ordered. The reasoning below is why it stayed out of scope at the time.)* Real line items would need an Avro schema evolution, a new `Orders.Api` request shape, and touching the most heavily-tested, foundational part of this whole lab - a much larger and riskier change than what this milestone is actually testing. `SagaSkuMapper` deterministically hashes each `OrderId` to one of Catalog/Inventory's nine seeded SKUs (quantity fixed at 1) as an explicit, documented stand-in for "which product this order is for," so the new steps have real stock to reserve against without that rewrite. The next milestone to touch this boundary honestly is Cart Service (M42) actually feeding real line items into checkout - out of scope here.

## What broke building this (found via CI and live deploy, not guessed)

**Total saga latency became impossible to measure honestly once there were multiple steps.** The original single-step design measured latency as `now - RequestedAt`, fine when there was only ever one request. With `RequestedAt` now reset on every step transition (needed for the timeout sweeper to have a meaningful per-step deadline), that same field could no longer answer "how long did the whole saga take" - only "how long did the *last* step take." Rather than add a separate `FirstRequestedAt` column purely for a latency number, the completion log was relabeled `finalStepLatencyMs` to say honestly what it measures rather than silently becoming wrong.

**The migration Job runs the `orders-api` image, not `orders-worker` - and I rebuilt the wrong one first.** `orders-worker` shipped with the new saga code but crash-looped immediately: `column "step" does not exist`. `orders-migrations-m7` (the `PreSync` hook that applies EF migrations) turned out to reference the `orders-api` image, which I hadn't rebuilt - it only shares `Orders.Infrastructure`, the project that actually owns the new migration, via a project reference. The migration job ran, reported "already up to date" against genuinely stale code, and `orders-worker`'s `BackgroundServiceExceptionBehavior: StopHost` took the whole process down on the first unhandled `PostgresException`. Fixed by rebuilding `orders-api` too (no application code changed - it just needed a fresh build to pick up the same shared migration) and force-deleting the crash-looped pod once the schema existed.

## Live results

All three saga outcomes exercised against the live cluster, through real `POST /orders` calls (Keycloak-authenticated) rather than synthetic Kafka messages:

**Happy path** - order for 49.90 BRL (well under the 1,000 decline threshold), mapped to `SKU-CLTH-002`:
```
OrchestratedSagaReservationRequested -> sku SKU-CLTH-002
OrchestratedSagaAdvanced -> step DecidePayment
OrchestratedSagaAdvanced -> step CommitInventory
OrchestratedSagaCompleted -> outcome=Confirmed, finalStepLatencyMs=49.06
```
`SKU-CLTH-002` went from `available=40` to `available=39, reserved=0` - a **permanent** deduction, not a returned-to-pool hold.

**Compensation path** - order for 1,500.00 BRL (over the threshold, declined), mapped to `SKU-HOME-001`:
```
OrchestratedSagaReservationRequested -> sku SKU-HOME-001
OrchestratedSagaAdvanced -> step DecidePayment
OrchestratedSagaAdvanced -> step ReleaseInventory   <- the compensation firing
OrchestratedSagaCompleted -> outcome=RejectedPaymentDeclined, finalStepLatencyMs=15.21
```
`SKU-HOME-001` ended at `available=25, reserved=0` - **exactly** its original seeded value. The reservation was made, held, and then genuinely undone, not just logged as if it were.

**Insufficient-stock path** - `SKU-ELEC-002` was already fully exhausted from Milestone 41's live oversell-prevention test (`available=0`). Twenty orders were fired to find two whose `OrderId` hash landed on that SKU; both were confirmed via direct inspection of `inventory.reservation-replied.v1`:
```json
{"reserved":false,"reason":"insufficient stock","sku":"SKU-ELEC-002", ...}
```
And confirmed via `payments.decision-requested.v1` that **zero** payment-decision messages were ever produced for either order - the short-circuit at step 1 means step 2 never runs at all, not that it runs and is ignored.

**Regression check**: `scripts/k6-run.sh smoke` post-deploy - `failed_rate=0`, `checks_rate=1`, `flow_rate=1`. The 4-step saga extension has no effect on the existing orders pipeline or the choreographed saga running alongside it.

## What's still out of scope

A saga that times out mid-flight (e.g. `DecidePayment` never replies) is logged and dropped by the sweeper, same as M22's original scope - it does not automatically compensate. Teaching the sweeper to also release a dangling reservation on timeout is a real production concern, but a separate piece of work from the deterministic, reproducible compensation path this milestone set out to prove (payment explicitly declining).
