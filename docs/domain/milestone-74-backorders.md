# Milestone 74: Waiting Is a State, Not a Cancellation

## The gap this closes

Milestone 72's plan asked for partial fulfilment; what shipped was all-or-nothing, on purpose — a partial reservation confirms an order the warehouse cannot actually fill. But all-or-nothing had a sharp edge nothing addressed afterward: "cannot fill it *right now*" and "will never be able to fill it" were treated identically. An order for more units than the network happens to hold at this instant was cancelled outright, indistinguishable from a SKU that will never restock. `WarehouseReplenishmentNeeded` (Milestone 73) already told the world a warehouse had gone low — nothing was listening, and nothing gave a waiting order anywhere to wait.

This milestone gives it somewhere: a new order status, a queue of what is owed, and a release path triggered by the same restocks that already flow through Inventory.Service.

## The state machine grows one node, in one place

```
Created ──reservation insufficient, but waitable──► Backordered ──restock covers it──► (saga continues) ──payment approved──► Confirmed
                                                                 └──restock never comes, or payment declines──► Cancelled
```

`Backordered`'s only predecessor is `Created`; `Confirmed` and `Cancelled` both gained `Backordered` as an additional legal predecessor. Nothing about `RunSideEffectsAsync` needed to change — it switches on the *target* status, and `SettlementActionFor(Backordered)` is `None` for the same reason `Confirmed` is: no money has moved. Payment is decided one saga step *after* reservation, so a backordered order was never charged in the first place — there is no hold to release, only a customer's wait to end.

## Reserve records a debt instead of a refusal

`StockAllocator.Allocate` returning unfulfillable used to be the end of the story. Now, when the SKU is real (an unknown SKU still fails outright — nothing will ever restock a SKU that does not exist, so backordering it would wait forever), `InventoryReservationMessageProcessor` records a `Backorder` row - keyed by the *same* `ReservationId` the saga already tracks - and replies `Reserved: false, Backordered: true`.

That one boolean is the entire saga-side change. `OrderSagaReplyConsumer` already had a branch for `!reply.Reserved`; it now checks `reply.Backordered` first. If true, the order moves to `Backordered` and — critically — the saga row is left exactly where it is, still parked at the `ReserveInventory` step. Nothing completes it, nothing cancels it.

## Release is a reply that looks like any other

The elegant part of this design is what it *didn't* need: no new event type, no new topic, no new consumer wiring on the Orders.Worker side. When a restock lands, `InventoryReservationMessageProcessor` checks for waiting backorders on that SKU and, for each one it can now cover, replays the exact same `InventoryReservationReplied` reply - same `ReservationId`, now `Reserved: true`. `OrderSagaReplyConsumer`'s existing success branch (`TryAdvanceAsync(orderId, ReserveInventory, DecidePayment, ...)`) fires exactly as if the original request had simply taken longer to answer. From the saga's point of view, it did.

### Decide-then-mutate, now in one place

Milestone 72 was bitten once by two call sites separately chaining "check, then mutate" and drifting apart - a reservation that mutated the warehouse network before the aggregate row got a chance to refuse. Rather than write that logic a second time for the release path, `WarehouseAllocationStore.TryReserveAsync` now consolidates it: check the network *and* the aggregate before mutating either, then mutate both. The Kafka-triggered reserve path and the backorder release path both call this one method. Two places independently getting "decide before mutate" right is how it stopped being right the first time; one place is how it stays right.

### FIFO, strictly

Release walks backorders for a SKU oldest-first and **stops at the first one it cannot yet cover** - it does not skip ahead to a smaller one further down the queue just because the stock happens to fit. Skipping ahead would be unfair to whoever has been waiting longest, and "unfair, occasionally, under exactly the conditions nobody tests for" is how a queue turns into a lottery. Proven with a restock sized to cover a *later, smaller* backorder but not the older, larger one in front of it: both stayed waiting.

## Giving up: the timeout sweeper

Waiting is not free for the customer. `BackorderTimeoutSweeper` claims backorders past a configurable window (`FOR UPDATE SKIP LOCKED`, same idiom as the reservation processor) and replies `Reserved: false, Backordered: false` on the same reservation id - which `OrderSagaReplyConsumer` treats as a permanent refusal, exactly the branch that already existed for "insufficient stock, unknown SKU." No new saga-side code here either.

It uses the same single-sweeper pattern Milestone 73 introduced for `PaymentAuthorizationSweeper`: `pg_try_advisory_xact_lock`, not a Kubernetes Lease. `SKIP LOCKED` already makes concurrent sweeps safe; the advisory lock exists only to stop every replica polling every tick, and needs no RBAC or in-cluster client to do it. Validated on the lab server by inserting a backorder two hours old directly and watching the next sweep tick claim and expire it.

## Three things that went wrong finding this out

None of these were bugs in the backorder logic itself - all three were in the surrounding scaffolding, and all three would have shipped silently if the milestone had stopped at "the code compiles."

### Two pre-existing integration tests were already broken

`InventoryReservationMessageProcessorTests`' fixture seeded an `InventoryItem` but never a `WarehouseStock` row. Since Milestone 72, `StockAllocator.Allocate` refuses outright on an empty candidate list - so `ProcessAsyncReservesWhenStockIsAvailable` and two others had been asserting a reservation succeeds while the code that decides reservations had nothing to allocate from. They were never run: this lab environment has no local Docker daemon, and Testcontainers needs one. The lab server does. Running the real suite there - the first time in this engagement the full integration suite had run anywhere - surfaced ten failures immediately, eight of them from this gap alone. Fixed by seeding a matching `WarehouseStock` row in both fixtures, restoring the invariant Milestone 72's own migration keeps on the real database: the aggregate and the network agree on how much exists.

### My own FIFO test's numbers didn't test what the name claimed

The first two drafts of `RestockReleasesBackordersOldestFirstAndStopsAtTheFirstThatStillDoesNotFit` used quantities where the "smaller, later" backorder was small enough to succeed *immediately at creation time*, before ever becoming a backorder at all - so there was only ever one row in the queue, and the test was vacuous. Fixed by depleting the shelf to zero first, so both requests are genuinely queued, and sizing the restock to cover the second in isolation but not the first - the only way to actually observe a skip-ahead if the code were wrong.

### A stale container silently ran the wrong saga mode

The most consequential finding wasn't in the code at all. Validating the full backordered-then-approved path requires `Saga__Mode: Orchestration` on *both* `orders-worker` and `payments-service` - Payments.Service gates its own orchestrated consumer (`PaymentDecisionRequestProcessor`) on the identical config key, independently. Flipping the value in `compose.yaml` and running `docker compose restart payments-service` looked like it worked - the container reported healthy - but `restart` re-runs a container's *existing* process image without reloading `compose.yaml`; only `up -d` recreates it against the current config. The result: a payment-decision-requested message sat in the topic with real consumer lag and no attached consumer, invisibly, while every health check passed. An order backordered, released, and then sat forever at `Backordered` with a `PaymentDecisionRequested` nobody would ever read.

Caught only because the order's final state didn't match what the code said should happen, and the discrepancy was chased through consumer group lag (`kafka-consumer-groups.sh --describe`) rather than assumed away. `docker exec ... env | grep Saga` on the running container was the confirming step - the compose file said `Orchestration`, the live process still said `Choreography`. `up -d`, not `restart`, is now the documented way to change one of these toggles.

## Validated on the lab server

- A 4-unit order against 2 units of network-wide stock: `Backordered`, reflected in both the database and `GET /orders/{id}` (cache correctly invalidated).
- A restock of 3 units, published directly to `inventory.restock-requested.v1`: the backorder cleared, the saga advanced through `DecidePayment`, and the order reached `Confirmed`.
- The same scenario at a 30-unit, high-value order: correctly declined once payment was actually decided, and correctly reached `Cancelled` from `Backordered` - the same terminal transition a `Backordered` order can take instead of `Confirmed`.
- The timeout sweeper: a backorder two hours old was claimed and expired on its next tick, logged, and removed.

## What this is not

- **No priority among waiting orders beyond arrival time.** A large customer's order does not jump a smaller one's queue - first come, first served, same fairness argument as the FIFO release itself.
- **No partial release.** A backorder either gets everything it asked for or stays waiting whole, for the same reason the original reservation is all-or-nothing.
- **No notification.** The customer is not told their order is waiting, only the order's status changes. A real system would email at `Backordered` and again at `Confirmed` or `Cancelled`; this lab's read model is the storefront's only channel.

## See also

- [Milestone 72: Stock Lives in Buildings](milestone-72-multi-warehouse-allocation.md) — the all-or-nothing decision this milestone gives a second chance to.
- [Milestone 73: Closing the Gaps the Plan Left Open](milestone-73-closing-the-plan-gaps.md) — `WarehouseReplenishmentNeeded`, the signal this milestone's restock path runs alongside, and the single-sweeper advisory-lock pattern this milestone's timeout sweeper reuses.
- [Milestone 69: The Order's Life Does Not End at Confirmed](milestone-69-order-lifecycle.md) — the transition table `Backordered` extends.
