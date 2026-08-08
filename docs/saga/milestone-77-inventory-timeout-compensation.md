# Milestone 77: Inventory Timeout Compensation Was the One Cancelled Order That Never Released Its Stock

## Scope

`SagaTimeoutSweeper`'s own doc comment (Milestone 36, unchanged since) admitted the gap outright: when the orchestrated saga times out - `Payments.Service` or `Inventory.Service` never replies - the sweeper voided any card hold and released the coupon (Milestone 69), but the inventory reservation itself was "still out of scope." Every other compensation path in this saga (a declined payment, an explicit commit failure, a real release reply) frees the stock it took. A timeout didn't. An order that got this far, then stalled, could sit `Cancelled` while `SKU-...`'s `reserved_quantity` stayed exactly where the reservation left it - permanently unavailable stock with no order to explain it, and no other path in the codebase that would ever notice or fix it.

## Design

**Not a blind release for every timed-out step.** The obvious fix - "always send a release" - turns out to be unsafe, and the reason lives in `WarehouseAllocationStore.TrySettleReservationAsync`: when it finds no per-reservation allocation row, it falls back to mutating the aggregate `InventoryItem` counters directly, a deliberate Milestone 72 migration-compatibility path that can't distinguish "this reservation genuinely never happened" from "this reservation predates per-warehouse tracking." Releasing a reservation that never actually reserved anything would decrement a real concurrent order's `ReservedQuantity` and conjure phantom `AvailableQuantity` out of nothing - a worse bug than the one this milestone closes. This was caught by reading `InventoryReservationMessageProcessor`/`WarehouseAllocationStore` before writing any sweeper code, not by a failing test.

**So the response is step-dependent, not uniform:**

| Timed-out step | Action | Why it's safe |
|---|---|---|
| `DecidePayment` | Publish a release, then cancel | A reservation certainly exists (the saga passed `ReserveInventory`) and certainly isn't committed yet (payment hasn't been decided) |
| `ReleaseInventory` | Re-publish the release, then cancel | A release was already requested once for this exact step; resending is a safe redelivery, not a guess |
| `CommitInventory` | Confirm, then move to `FulfillmentHold` - no release | Payment was already approved to reach this step, so the order is real, but whether the commit landed is genuinely unknown from here. Guessing wrong in either direction either loses inventory or corrupts someone else's count, so this doesn't guess - it lands in the same "needs a human" state `HandleCommitRepliedAsync`'s explicit `Committed:false` branch already uses |
| `ReserveInventory` (default) | Cancel only, no release attempted | Whether anything was ever reserved is unknown. This is the one gap the milestone leaves open rather than paper over - see below |

`FulfillmentHold` reuses the exact status Milestone 76 built for the settlement-mismatch case: "confirmed or shipped, but something a human needs to look at," not a new concept.

## What this still doesn't fix

A timeout at `ReserveInventory` still can't tell whether `Inventory.Service` ever received the request at all - it may have crashed before replying, or its reply may be delayed rather than lost. Releasing blindly here has the exact same `WarehouseAllocationStore` fallback risk described above, so the sweeper does what the pre-M77 code did for every step: cancel the order and stop. If `Inventory.Service` did reserve something, that stock stays reserved with no order attached to it until someone looks. Closing this for real needs `Inventory.Service` to answer "did I ever see reservation X" authoritatively (a durable idempotency record keyed by reservation ID, independent of whether the aggregate-fallback path fired) - which is really the same debt `TrySettleReservationAsync`'s M72-compat fallback already carries, not a new one this milestone introduces.

## Changes

- `apps/src/Orders.Worker/SagaTimeoutSweeper.cs` - `ResolveAsync` (made `public`, same testability shape as `OrderSagaOrchestrator.RequestReservationAsync` and the `*MessageProcessor` classes) now branches on `saga.Step` per the table above, instead of unconditionally calling `TryCancelAsync` for everything. Class doc comment rewritten to carry the safety analysis.
- `apps/src/Orders.Worker/OrderSagaOrchestrator.cs` - new `TimeoutReleaseRequested` log line (EventId 6015) on the `SagaOrchestratorLog` partial class.
- `apps/tests/Orders.IntegrationTests/SagaTimeoutSweeperTests.cs` - new. Four tests against real Postgres + Redpanda (Testcontainers), one per branch in the table: `DecidePayment` and `ReleaseInventory` assert both the resulting `Cancelled` status and a real `InventoryReservationReleaseRequested` message on the wire; `CommitInventory` asserts `FulfillmentHold` and that nothing was published to the release topic; `ReserveInventory` asserts `Cancelled` with nothing published either.
- `apps/tests/Orders.UnitTests/OrderStatusTransitionTests.cs` - unrelated regression, found while running the full suite for this milestone (see below), fixed alongside it.

## A second regression, found running the full suite for this milestone

`OrderStatusTransitionTests.MoneyIsOnlyEverAskedToBeCapturedOnceAndVoidedOnceAlongAnyPath` - the property test Milestone 76 had already relaxed once - failed again on a different random seed, shrunk to `[Confirmed, Picking, Shipped, FulfillmentHold, Picking, Shipped, Confirmed]`. Milestone 76 made `Shipped -> FulfillmentHold` legal; `FulfillmentHold -> Picking -> Shipped` was already legal (Milestone 69: ops routes a held order back through fulfilment). Combined, `Shipped` is now reachable more than once in a single path - a real, intended sequence (a re-picked and re-shipped order after a human resolves whatever put it on hold), not a graph bug - and each visit asks for another capture. `voids <= 1` still holds structurally (`Cancelled` is terminal, never a predecessor of anything), but `captures <= 1` no longer does, and unlike the M76 case this isn't bounded by the graph at all - it's bounded only by how many times ops chooses to cycle the order.

Fixed by dropping the capture-count assertion (renaming the test to `VoidIsRequestedAtMostOnceButRepeatedCaptureRequestsAreSafeOnlyBecausePaymentItselfIsIdempotent`) rather than trying to bound something that isn't actually bounded. The real guarantee - a repeated capture request never charges twice - already lives one layer down and is already checked: `PaymentAuthorizationTests.CapturingAnAuthorizationMovesTheMoneyOnce` proves `Payment.TryCapture` only succeeds once regardless of how many times it's called, because it guards on the payment's actual state, not a count. This milestone didn't need a new domain test - the existing one already proves the thing the graph can no longer promise.

## Live validation (real Postgres + Kafka, not just Testcontainers)

`SagaTimeoutSweeper` is gated on `LeaderElectionService.IsLeader` (Milestone 36), which requires a real Kubernetes Lease - it never becomes leader in Compose (`KubeConfigException: Unable to load in-cluster configuration`, already a known, pre-existing, logged condition, unrelated to this milestone), so the sweeper's polling loop has never actually fired inside this lab's Compose deployment. The lab's K3s cluster (where leader election would work) is currently stale - every application pod is `Unknown` status, last active 7+ days ago - so that path wasn't available either.

Rather than claim a validation that didn't happen, `ResolveAsync` was exercised directly against the real, running Compose Postgres and Kafka: a throwaway console harness (referencing `Orders.Worker` directly, not reimplementing its logic) ran inside a `dotnet/sdk:10.0` container attached to the stack's own Docker network, seeded one real `saga_orchestration_states` row per step via the actual `SagaOrchestrationStore`, and called the actual `SagaTimeoutSweeper.ResolveAsync` against the live database and broker. This is the same code the deployed `orders-worker` image runs; only the leader-election gate and the polling loop around it were bypassed, since those are Milestone 36's concern, not this one's.

```
DecidePayment      -> release published, status Cancelled
ReleaseInventory   -> release re-published, status Cancelled
CommitInventory    -> status FulfillmentHold, nothing published
ReserveInventory   -> status Cancelled, nothing published
```

Cross-checked in `Inventory.Service`'s own logs for the two release cases - it genuinely received and processed both messages end-to-end through the real outbox/inbox pipeline:

```
Inventory.Service: Decided release {reservationId} for sku SKU-M77-VALIDATION: released=False
                    with correlation m77-live-validation-DecidePayment
Inventory.Service: Decided release {reservationId} for sku SKU-M77-VALIDATION: released=False
                    with correlation m77-live-validation-ReleaseInventory
```

`released=False` is correct and expected: the harness used a SKU that was never actually reserved (synthetic validation data), so `Inventory.Service` correctly found nothing to release - proving the message pipeline works without needing to fabricate a real reservation just to exercise it. The synthetic orders and saga rows were deleted from Postgres after validation; nothing from this session was left in the live database.

## Test suite

Full solution, real Testcontainers, on the lab server:

```
Orders.ContractTests         3 passed
Storefront.UnitTests         8 passed
Cart.IntegrationTests        4 passed
Catalog.IntegrationTests     7 passed
Services.ArchitectureTests  80 passed
Orders.ArchitectureTests    79 passed
Orders.IntegrationTests     34 passed  (4 new: SagaTimeoutSweeperTests)
Inventory.IntegrationTests  13 passed
Orders.UnitTests           165 passed  (1 renamed+relaxed, reasoning above)
```

393/393, 0 failures.

## What this unblocks

Milestone 78 (multi-line saga support) inherits this compensation logic directly - once `SagaOrchestrationState` tracks more than one line, each line's timeout-compensation still needs the same per-step safety reasoning this milestone worked out, just applied per line instead of once per order.
