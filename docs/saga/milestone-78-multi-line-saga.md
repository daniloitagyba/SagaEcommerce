# Milestone 78: Every Line Gets Reserved Now, Not Just the Biggest One

## Scope

Since Milestone 66, `OrderSagaOrchestrator` reserved exactly one line per order - "the largest by value," a stated and previously honest simplification, because `SagaOrchestrationState` had one `Sku`/`Quantity`/`ReservationId` column, full stop. A two-line order for a 500 BRL item and a 50 BRL item only ever checked stock for the 500 BRL item; the 50 BRL item shipped (or didn't) with nobody having asked Inventory.Service whether it existed. This milestone makes the saga track and reserve every line.

## Design

**One row per line, not one column set per order.** `SagaOrchestrationState` (the parent) keeps the order-level fields - `CorrelationId`, `CustomerId`, `PaymentMethod`, `Amount`, `Step`, the things `PaymentDecisionRequested` needs, none of which are per-line. `Sku`/`Quantity`/`ReservationId` moved out to a new child table, `saga_orchestration_lines` (migration `AddSagaOrchestrationLines`), one row per SKU, `ON DELETE CASCADE` off the parent so completing or timing out a saga removes every line with it. Each line gets three nullable outcome columns - `reserved`, `committed`, `released` - null until that line's own reply arrives.

**The order-level 4-step state machine is unchanged.** `ReserveInventory -> DecidePayment -> CommitInventory -> (done)` is still exactly what it was. What changed is what "the reply for this step arrived" means: it used to mean one Kafka message; now it means *every* line's message. `OrderSagaReplyConsumer` records one line's outcome via `SagaOrchestrationStore.RecordLineOutcomeAsync` (guarded by `... AND {column} IS NULL`, the same redelivery-safe CAS pattern `TryAdvanceAsync` already used on `Step`), then re-reads every line for that order to decide: still waiting on someone, or does the parent row get to advance.

**Partial-failure compensation is the one genuinely new behavior.** If line A reserves fine and line B is rejected outright (not backordered - genuinely out of stock), the whole order fails, same as always - but now there's something to clean up: line A's reservation. `HandleReservationRepliedAsync` publishes a release for every sibling line that already reserved before cancelling the order. Without this, a two-line order where only the second line was unavailable would have left the first line's stock held forever, with no path in the system that would ever release it.

**Backorder holds every already-reserved sibling, doesn't release them.** If line A reserves and line B backorders, the order moves to `Backordered` (Milestone 74's existing status) but line A's reservation stays exactly as it is - released only if the order is eventually cancelled outright (a rejection or a sweeper timeout), not because a sibling is still waiting. Releasing it just because another line hasn't arrived yet would be strictly worse than holding it: it would have to be re-requested the moment B clears, for no benefit.

**`SagaTimeoutSweeper` generalizes the same way it already did for Milestone 77.** A `DecidePayment`/`ReleaseInventory` timeout now loops `saga.Lines` and releases every one, instead of the one reservation Milestone 77 assumed. The safety reasoning from that milestone (every line at those two steps is certain to exist and certain not to be committed yet) applies per line unchanged.

## What didn't need to change

**The TLA+ model (`OrderSagaGuarded.tla`, Milestone 56) and the deterministic simulation (`SagaDeterministicSimulationTests.cs`, Milestone 58) are untouched, deliberately.** Both model the order-level state machine as five abstract states and treat "the reservation step succeeded" as one atomic event - neither ever modeled how many concrete Kafka messages that required. Fanning `ReserveInventory` out to N line replies instead of one is exactly the kind of implementation detail both artifacts' own doc comments already disclaim ("not the full I/O stack a production framework would virtualize"). The `NoResurrection` property they verify - a saga, once `Done`, never leaves `Done` - is still exactly as true with N lines as with one, for the same reason it was true before: the guard is on `Step`, and `Step` still only ever has one writer-in-waiting per order.

**The wire contracts (`InventoryContracts.cs`) are untouched.** `InventoryReservationRequested`/`Replied`/`CommitRequested`/`ReleaseRequested` already carried a `ReservationId` unique per request. Making that ID unique per *line* instead of per *order* was enough to route every reply back to the right row - no `LineIndex` field needed on the wire.

## Changes

- `apps/src/Orders.Domain/SagaOrchestrationLine.cs` - new entity, one row per line.
- `apps/src/Orders.Domain/SagaOrchestrationState.cs` - `ReservationId`/`Sku`/`Quantity` removed (moved to the line entity).
- `apps/src/Orders.Infrastructure/Data/OrdersDbContext.cs` - `SagaOrchestrationLine` mapped, FK cascade to the parent, unique index on `reservation_id` (how a reply gets routed back to its line).
- `apps/src/Orders.Infrastructure/Data/Migrations/20260808104604_AddSagaOrchestrationLines.cs` - drops the three columns from `saga_orchestration_states`, creates `saga_orchestration_lines`.
- `apps/src/Orders.Worker/SagaOrchestrationStore.cs` - `SagaOrchestrationRecord.Lines` replaces the single `ReservationId`/`Sku`/`Quantity`. `TrackReserveRequestedAsync` now takes a line list (parent + N line rows in one transaction). New `RecordLineOutcomeAsync`/`GetLinesAsync`. `ClaimTimedOutAsync` restructured (candidate-lock, then read, then delete - three round trips instead of one clever RETURNING, to avoid relying on undefined-feeling CTE/cascade timing).
- `apps/src/Orders.Worker/OrderSagaOrchestrator.cs` - reserves every line in `orderCreated.LinesOrEmpty`, not the single largest.
- `apps/src/Orders.Worker/OrderSagaReplyConsumer.cs` - every reply handler generalized to record-one-line-then-check-all-lines; `HandleReservationRepliedAsync` gained the partial-rejection compensation path described above.
- `apps/src/Orders.Worker/SagaTimeoutSweeper.cs` - releases every line on a `DecidePayment`/`ReleaseInventory` timeout.
- Tests: `SagaOrchestrationStoreTests.cs` updated for the new store shape plus a new multi-line tracking test; `OrderSagaOrchestratorTests.cs` gained a test proving both lines of a multi-line order get reserved, not just the larger one; `OrderSagaReplyConsumerMultiLineTests.cs` - new, proves the saga waits for every line before advancing and that an outright rejection releases an already-reserved sibling; `SagaTimeoutSweeperTests.cs` updated for the new seeding API.

## Live validation (real Compose stack, real orders)

Two real orders through `POST /orders`, `Saga:Mode=Both`, against the freshly deployed `orders-worker` (migration applied first, table verified empty before migrating - a live in-flight saga's columns getting dropped out from under it was the one thing to avoid here).

**Happy path** - 1× `SKU-BOOK-001` + 1× `SKU-BOOK-002`:

```
POST /orders -> 201, both lines priced
OrchestratedSagaReservationRequested  sku SKU-BOOK-001
OrchestratedSagaReservationRequested  sku SKU-BOOK-002
OrchestratedSagaAdvanced              step DecidePayment      (only after both replied)
OrchestratedSagaAdvanced              step CommitInventory
OrchestratedSagaCompleted             outcome=Confirmed
GET /orders/{id}: status "Confirmed"
```

**Partial-failure compensation, for real** - 2× `SKU-BOOK-001` + 1× `SKU-ELEC-001` (high value, triggered a payment decline):

```
OrchestratedSagaReservationRequested  sku SKU-BOOK-001
OrchestratedSagaReservationRequested  sku SKU-ELEC-001
OrchestratedSagaAdvanced              step DecidePayment      (both lines reserved)
OrchestratedSagaAdvanced              step ReleaseInventory   (payment declined - both lines released)
OrchestratedSagaCompleted             outcome=RejectedPaymentDeclined
GET /orders/{id}: status "Cancelled"
```

`SKU-BOOK-001`/`SKU-ELEC-001` available quantities returned to exactly their pre-order values after the cancellation - both lines' reservations were released, not just one. This order didn't hit the outright-rejection compensation path specifically (payment declined, not a line rejected), but it did exercise the same "release every line" fan-out `HandlePaymentDecisionRepliedAsync` now does, live, for the first time with more than one line involved.

No errors in `orders-worker`, `inventory-service`, or `payments-service` logs across either run.

## Test suite

Full solution, real Testcontainers, on the lab server:

```
Orders.ContractTests         3 passed
Storefront.UnitTests         8 passed
Cart.IntegrationTests        4 passed
Catalog.IntegrationTests     7 passed
Services.ArchitectureTests  80 passed
Orders.ArchitectureTests    81 passed  (+2: new SagaOrchestrationLine picked up by the fitness functions)
Orders.IntegrationTests     39 passed  (5 new: 1 SagaOrchestrationStoreTests, 1 OrderSagaOrchestratorTests, 3 OrderSagaReplyConsumerMultiLineTests)
Inventory.IntegrationTests  13 passed
Orders.UnitTests           165 passed
```

400/400, 0 failures.

## What this leaves open

A rejection that arrives while a sibling line is still genuinely in-flight (not yet replied at all, not backordered) isn't retroactively compensated - only lines already marked `Reserved == true` get released. This is correct, not a gap: a line that hasn't replied yet hasn't reserved anything to release. If it later replies `Reserved: true` for an order whose saga row is already gone (deleted by the rejection's `TryCompleteAsync`), `RecordLineOutcomeAsync` finds no matching row and no-ops - the same "line row must still exist" guard every other late/duplicate reply in this file already relies on - but that reservation itself is never explicitly released, on the same footing as Milestone 77's own acknowledged `ReserveInventory`-timeout gap: whether Inventory.Service ever actually reserved it is unknown from here, and guessing wrong risks corrupting an unrelated order's count.
