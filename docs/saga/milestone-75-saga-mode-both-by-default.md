# Milestone 75: `Saga:Mode=Both` Is the Default Now, Not `Choreography`

## Scope

An architecture review of the whole system (not a single feature) turned up something the code was honest about but the deploy configuration hid: `Saga:Mode` has defaulted to `Choreography` since Milestone 65 introduced the toggle, in both `compose/compose.yaml` and — implicitly, since it was never set at all — every `kubernetes/base/*.yaml` manifest. The choreographed path (`OrderMessageProcessor` in `Payments.Service`) decides on the order's amount alone; it has no inventory step at all. That means every order this lab has confirmed through Compose or the K3s cluster, since M65, skipped `Inventory.Service` entirely — no stock check, no reservation, no decrement. `Inventory.Service`'s partitioning (M41), rebalance-vs-serialization proof (M51), linearizability check (M57), multi-warehouse allocation (M72), and backorders (M74) are all real and all tested, but none of them were reachable from a `POST /orders` call in the environment this lab actually runs.

This milestone closes that gap the cheap way: change the default, not the architecture. `Saga:Mode=Both` was already implemented and validated (see the M65 update in `docs/saga/milestone-22-orchestration-vs-choreography.md`) — it just wasn't switched on.

## Why `Both`, not `Orchestration`

Three options were on the table:

- **`Orchestration`** would turn off the choreographed path entirely. That's the smallest blast radius for *this* fix, but it throws away the side-by-side comparison M22 exists to make, and it means the older, simpler path never gets exercised again.
- **Coreography reserving inventory too** would mean duplicating the reservation logic in `OrderMessageProcessor`, weakening the comparison in the other direction — the two paths would converge toward doing the same thing instead of being genuinely different implementations of the same contract.
- **`Both`** keeps both implementations honest: they race against the same order, `OrderStatusStore`'s guarded `UPDATE ... WHERE status = 'Created'` makes the race idempotent (whichever reply lands first wins, the other is a no-op), and the orchestrated path — the one with a real inventory step — is the one actually deciding whether stock exists. This is the option that fixes the bug without deleting the thing M22 was built to demonstrate.

## Changes

- `compose/compose.yaml` — `Saga__Mode: Both` for `orders-worker` and `payments-service` (was `Choreography` in both).
- `kubernetes/base/orders-worker.yaml`, `kubernetes/base/payments-service.yaml` — `Saga__Mode: Both` added explicitly. It was absent from both, relying on the code's own default (`Choreography`) — the same class of implicit-default bug this lab has hit repeatedly (`Redis__ConnectionString`, `Authentication__Authority`, four separate Kafka `BootstrapServers` sections — see `artifacts/lab-server.md`, not versioned). Explicit now, so a future default change in code can't silently change deployed behavior again.
- `kubernetes/base/payments-service.yaml` — also fixed a second, unrelated instance of that same bug class found while touching this file: `PaymentSettlement__BootstrapServers` was missing entirely (present in Compose, absent in K8s). `PaymentSettlementOptions` defaults `BootstrapServers` to `localhost:9092`, which resolves to the pod itself, not the `kafka` Service — this would have made capture/void/refund consumption silently unreachable in the cluster. Never actually deployed against K3s since M68 added it, so never caught.
- `apps/src/Orders.Worker/OrderSagaOrchestrator.cs` — `RequestReservationAsync` changed from `private` to `public`, the same shape every `*MessageProcessor` class in this codebase already uses, specifically so it's unit-testable without standing up the full `BackgroundService` Kafka consumer loop.
- `apps/tests/Orders.IntegrationTests/OrderSagaOrchestratorTests.cs` — new. Two tests against real Postgres + Redpanda (Testcontainers): an order with line items produces a `saga_orchestration_states` row for the *real* SKU/quantity and an actual `InventoryReservationRequested` message on `inventory.reservation-requested.v1`; an amount-only order (no lines) is skipped rather than given an invented reservation. This is the regression test that would have caught the original gap: it fails if an order's line items never reach Inventory at all.

## What was already true and stayed untouched

The saga still tracks only the largest line by value (`SagaOrchestrationState` has one `Sku`/`Quantity`, not a collection) — a stated, documented simplification since M66, not something this milestone changes. A multi-line order now reserves its biggest line for real, instead of reserving nothing; the other lines are still unchecked. That's Milestone 78.

## Live validation (Compose, real cluster)

Rebuilt `orders-worker` and `payments-service`, applied via `docker compose up -d` (not `restart` — restart reuses the already-materialized env from the running container and would have silently kept the old `Saga__Mode`, a mistake documented from M74's own validation).

**Confirm path** — `POST /orders` for 2× `SKU-BOOK-001` (a real, low-risk order):

```
SKU-BOOK-001 before: available=98, reserved=0
→ 201 Created, status "Created"
→ (saga runs)
→ GET /orders/{id}: status "Confirmed"
SKU-BOOK-001 after:  available=96, reserved=0
```

Two units gone from `available`, none sitting in `reserved` — a full reserve → commit, not a hold. `saga_orchestration_states` has no row for this order afterward (`TryCompleteAsync` removed it on the `CommitInventory` step, as designed).

**Compensation path** — `POST /orders` for 3× `SKU-ELEC-001` (12,254.72 BRL, over every risk threshold — first purchase + high value):

```
SKU-ELEC-001 before: available=47, reserved=0
→ 201 Created
→ (saga runs, payment declined)
→ GET /orders/{id}: status "Cancelled"
SKU-ELEC-001 after:  available=47, reserved=0
```

Reserved, then released back to exactly the starting `available` — the compensating transaction M43 built, now actually reachable from a real checkout instead of only from `Saga:Mode=Orchestration` manual testing.

No new errors in either service's logs across both runs, past the two pre-existing, already-documented ones unrelated to this change (Pyroscope's native-profiler log-file warning; `LeaderElectionService`'s expected `KubeConfigException` outside a real K8s pod).

## Test suite

Full solution, real Testcontainers, on the lab server (which carries the matching `10.0.302` SDK):

```
Orders.ContractTests        3 passed
Storefront.UnitTests        8 passed
Services.ArchitectureTests 80 passed
Orders.ArchitectureTests   79 passed
Cart.IntegrationTests       4 passed
Orders.IntegrationTests    25 passed  (2 new: OrderSagaOrchestratorTests)
Inventory.IntegrationTests 13 passed
Catalog.IntegrationTests    7 passed
Orders.UnitTests          163 passed
```

382/382, 0 failures.

## What this unblocks

Milestone 77 (compensating the *inventory* reservation when a saga times out mid-flight, not just voiding the payment hold) and Milestone 78 (reserving every line of a multi-line order, not just the largest) both depend on the orchestrated path actually running in the deployed environment. Before this milestone, both would have been correct code that nothing exercised.
