# Milestone 81: Cancelling an Order Gives Back Everything It Took

## Scope

`Cancelled` has been reachable from five states - `Created`, `Confirmed`, `Picking`, `FulfillmentHold`, `Backordered` - since Milestone 69 widened the transition table. The compensation that runs on the way there was written for exactly one of them. An audit of every production path that can reach `Cancelled` turned up three money-and-stock leaks, all the same shape: code written when `Cancelled` had one predecessor, never revisited when it grew four more.

1. **A cancelled Pix order kept the shopper's money.** `OrderStatusStore.RunSideEffectsAsync` gated the whole settlement branch on `PaymentMethods.RequiresCapture(paymentMethod)` - true for Card and Boleto, false for Pix. But Pix is the one method that is `Captured` the instant it's approved (`Payment.Authorize`), so it is exactly the method for which cancelling *must* produce a refund. The guard skipped it entirely. `EfOrderStatusRepository` (the operator-driven fulfilment path) had the identical gate.
2. **Cancelling after `Confirmed` never returned the stock.** Inventory commits (draws units permanently out of stock) the moment an order reaches `Confirmed`. Nothing on the `Confirmed → Cancelled`, `Picking → Cancelled`, or `FulfillmentHold → Cancelled` edges told Inventory anything - `InventoryReservationReleaseRequested` is only ever published from the saga's own pre-commit compensation paths, and `InventoryRestockRequested` only from a customer return, which requires `Delivered`. Silent, and unbounded: the gap widens with every operator-cancelled order once it has left `Created`.
3. **Cancelling a `Backordered` order left its backorder waiting forever.** The FIFO release path (`ReleaseBackordersAsync`) has no idea the order it's about to reserve stock for was cancelled hours ago - it would reserve on its behalf anyway, ahead of whoever is actually still waiting, and nothing would ever release the phantom reservation back.

All three are fixed the same way: stop asking "which payment method is this" or assuming a single predecessor, and instead ask what state the *thing being compensated* - the payment, the inventory - actually holds, right now.

## Design

**`Payment.TryCancel` replaces the method-keyed void dispatch.** A cancellation is not "void the hold" - it's "make sure nothing is owed," and which of the two genuinely different actions that takes depends on the payment's own current state, not the order's payment method:

```
Authorized / AwaitingPayment  →  void          (a card or boleto hold that was never charged)
Captured                      →  refund in full (Pix - or a card/boleto that, per today's state
                                                  machine, can never actually reach here Captured -
                                                  see "What this doesn't handle" below)
Declined / Expired /
Voided / Refunded             →  no-op          (nothing was ever owed, or it's already settled)
```

`TryRefund(RefundableAmount, now)` reuses the exact cumulative guard a return already relies on, so a cancellation landing on a payment a return has already partially refunded gives back only what remains. One new command, `PaymentCancellationRequested`, replaces `PaymentVoidRequested` at both of its production call sites (`Orders.Worker/OrderStatusStore` and `Orders.Infrastructure/EfOrderStatusRepository`) - and since nothing produces `PaymentVoidRequested` any more, it's deleted outright along with its topic, options, and dispatch case, rather than left as a dead code path nobody will notice rotting.

`PaymentSettlementProcessor`'s existing "is this a harmless redelivery or a genuine mismatch that needs the saga's attention" branch (Milestone 76) gets a `Cancel` case that is always the benign redelivery answer - unlike a capture, cancellation has no single target state, so "did it apply" can't be compared against one expected outcome. Every state `TryCancel` refuses to move from means there is genuinely nothing left to do, never that money should have moved and silently didn't.

**Inventory compensation is chosen from where the order actually was**, not guessed from the target status alone. `EfOrderStatusRepository` - the only path that can reach `Cancelled` from a post-commit state, since every `Orders.Worker`-driven cancellation is always from `Created` (see "Why Orders.Worker needed no inventory change" below) - now captures the *previous* status atomically with the transition itself:

```sql
WITH previous AS (
    SELECT status, payment_method FROM orders WHERE id = @id FOR UPDATE
)
UPDATE orders o SET status = @status
FROM previous
WHERE o.id = @id AND previous.status = ANY(@allowed_from)
RETURNING previous.status, previous.payment_method
```

The `FOR UPDATE` matters, not just the `RETURNING`: `Cancelled`'s predecessor set has five members, and a plain read-then-CAS-write (the shape every other transition in this codebase uses) leaves a real gap where a *different*, also-legal predecessor could land between the read and the write - misidentifying, say, a `Backordered`-origin cancellation as `Confirmed`-origin, and picking the wrong compensation for it. The lock closes that gap; the existing `status = ANY(...)` guard still catches the ordinary lost-race case. This needed raw ADO.NET rather than EF's `ExecuteSqlInterpolatedAsync`, which only ever returns an affected-row count - the same reason `Orders.Worker`'s `OrderStatusStore` already talks to Npgsql directly for its own CAS; this milestone extends that established pattern to a second file rather than inventing a new one.

From the captured previous status:

- **`Confirmed` / `Picking` / `FulfillmentHold`** → one `InventoryRestockRequested` per order line, through the outbox, in the same transaction as the status change. Reuses Inventory's existing restock handler wholesale; from Inventory's side a cancellation-restock and a return-restock are the same operation.
- **`Backordered`** → a new `BackorderCancellationRequested(OrderId)`, consumed by Inventory as a single `DELETE FROM backorders WHERE order_id = @orderId` inside its own inbox-guarded transaction. Deliberately not a release-and-reply: nothing was ever reserved for a row still sitting in the backorder table (see the class comment on `ReleaseBackordersAsync`'s own reservation call), so there is nothing to release, only the wait itself to give up, and nothing downstream is waiting on a reply to an order that's already `Cancelled`. The delete is idempotent by construction - redelivery just deletes zero rows the second time - so the inbox row exists for observability, not correctness.
- **`Created`** → deliberately nothing. See below.

**Why `Orders.Worker` needed no inventory change.** Every call site of `OrderStatusStore.TryCancelAsync` - `PaymentResultProcessor`'s decline branch, `SagaTimeoutSweeper`'s two release-then-cancel cases, `OrderSagaReplyConsumer`'s two compensation branches - transitions from `Created`, and `SagaOrchestrationState` (the orchestrated saga's own tracking row) is deleted at the `CommitInventory` step, before `Confirmed` is ever reached. There is structurally no path from `Orders.Worker` to a post-commit cancellation. Worker's fix is therefore payment-only: the same `RequiresCapture`-gated dispatch had the identical Pix bug, fixed the same way.

**What this doesn't fix: an operator cancelling an order the saga is still mid-flight on.** `Created → Cancelled` is reachable through `EfOrderStatusRepository` too (an operator can `POST /orders/{id}/fulfillment {"status":"Cancelled"}` on any order, including one the saga hasn't confirmed yet), and this milestone deliberately does nothing for inventory on that edge. The saga's own reservation state lives in `Orders.Worker`, invisible to `Orders.Api`; guessing at what to release or restock from here risks conjuring stock that belongs to a decision already in flight elsewhere, or destroying a real concurrent order's count - the exact failure mode `SagaTimeoutSweeper`'s own design comment (Milestone 77) already ruled out for the *symmetric* case of releasing an unknown-state `ReserveInventory` timeout. That race predates this milestone and remains an open gap, not a regression it introduces.

## A related bug, same family, caught while building this

`EfOrderReturnRepository.QueueRestockCommands` - the Milestone 70 returns path this milestone's restock code deliberately mirrors - queued one `InventoryRestockRequested` per SKU in a return, but gave every one of them the *same* id: `orderReturn.Id`. Inventory's restock handler deduplicates on that id via the inbox (`ON CONFLICT (consumer_name, event_id) DO NOTHING`). A multi-SKU return therefore restocked its first SKU and silently dropped every SKU after it as an apparent duplicate of the first - the stock for those units never came back. Writing the equivalent cancellation-restock code and reaching for the same "what id does this line get" question surfaced it. Both call sites now mint a fresh `Guid` per line.

## Verification performed

This environment has no Docker daemon, so none of Testcontainers-backed integration suite, the Compose stack, or a live cluster were available this pass - everything below is build- and unit-test-level, not the live curl-against-a-running-service validation every other milestone in this directory records. That gap is real and is the honest state of it, not glossed over.

- **Full solution build**: `dotnet build SagaEcommerce.slnx`, 0 warnings, 0 errors (this repository treats warnings as errors, so a nullable-reference slip or an unused-code warning would have failed it).
- **`Orders.UnitTests`**: 175/175 passing, including 10 new `Payment.TryCancel` facts covering every state transition in the table above - an authorized card voiding, an awaiting boleto voiding, a captured Pix refunding in full, a partially-refunded payment refunding only the remainder, a declined payment producing a benign no-op (not a mismatch) for all three methods, an already-expired hold staying untouched, and double-cancellation not double-refunding.
- **`Orders.ArchitectureTests`** and **`Services.ArchitectureTests`**: 81/81 and 80/80 passing - the module-size and layering fitness functions this codebase runs are unaffected by the new file's size or its dependencies.
- **New integration tests, written but not run**: three in `PaymentSettlementProcessorTests` (Pix-capture-then-cancel-refunds, card-authorized-then-cancel-voids, declined-payment-cancel-is-silently-dropped) and two in `Inventory.IntegrationTests/BackorderTests` (cancelling a waiting backorder removes it and a later restock does not reserve on its behalf; cancelling an order with no backorder is a harmless no-op). These compile against the real domain and processor types and follow the existing Testcontainers fixture pattern exactly; they have not been executed against a real Postgres.
- **Not verified at all in this pass**: the `FOR UPDATE`-based CTE in `EfOrderStatusRepository` against a real Postgres instance (correct per standard Postgres locking-clause semantics, but unexercised here), the Kafka topic wiring end to end, and the TLA+ saga model (Milestone 56) was not extended to include the operator-driven cancellation paths this milestone adds - it still models only the saga's own compensation, not compensation triggered from outside it.

## What was deliberately not done

- **The `Created`-origin operator-cancellation race** described above.
- **A cancellation reason beyond the fixed `"order cancelled"` string** carried through to `PaymentCancellationRequested.Reason` and the restock/backorder-cancellation commands - an operator or shopper-facing cancellation reason is a Milestone 83 concern (self-service cancellation), not this one.
- **Extending the TLA+ model.** Real production risk given this milestone changes compensation logic in the saga's own neighborhood, and explicitly flagged rather than silently skipped.

## See also

- [Milestone 69: The Order's Life Does Not End at Confirmed](milestone-69-order-lifecycle.md) — the transition table this milestone's compensation now actually covers.
- [Milestone 70: Returns, Partial Refunds, and a Money Bug in Shipped Code](milestone-70-returns-and-refunds.md) — `ReturnRefundCalculator` and the restock path this milestone's restock-on-cancel reuses (and whose multi-SKU id bug this milestone also fixed).
- [Milestone 73: Closing the Gaps the Plan Left Open](milestone-73-closing-the-plan-gaps.md) — the `pg_try_advisory_xact_lock` single-sweeper pattern and the "one new thing wired in wrong, the whole outbox stops" shape this milestone's own bugs shared.
- [Milestone 74: Waiting Is a State, Not a Cancellation](milestone-74-backorders.md) — the backorder table and FIFO release path this milestone's cancellation now interrupts correctly.
- [Milestone 76: A Capture That Fails Is Now Visible, Not Silent](milestone-76-settlement-reconciliation.md) — the redelivery-vs-mismatch distinction this milestone's `Cancel` operation extends.
- [Milestone 77: Inventory Timeout Compensation Was the One Cancelled Order That Never Released Its Stock](../saga/milestone-77-inventory-timeout-compensation.md) — the "don't guess when you can't see the state" argument this milestone's `Created`-origin gap deliberately follows.
