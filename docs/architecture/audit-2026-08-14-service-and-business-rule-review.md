# Service and Business-Rule Audit — Remediation Plan (2026-08-14)

A read-through of every service's business rules and cross-service contracts,
looking for loose ends (a path that starts and never finishes) and misapplied
patterns (a pattern that is present but does not actually hold the invariant it
was introduced for).

Scope: `Orders.{Api,Application,Domain,Infrastructure,Worker}`,
`Payments.Service`, `Inventory.Service`, `Cart.Service`, `Storefront.Service`,
`BuildingBlocks.*`, plus the Compose/Kubernetes configuration that decides which
code paths are actually reachable.

The working tree at the time of the audit carries an unfinished
`OrderStatusChanged` change (new contract + outbox row + projection/event-store
handling). Findings 3 and 4 below are directly about that work; the plan folds
it in rather than around it.

---

## Findings, ranked

| # | Finding | Severity | Where |
|---|---|---|---|
| 1 | The CQRS read model and the event-store timeline never leave `Created` in any deployed configuration | **P0** | `Payments.Service/Program.cs:150`, `Orders.Worker/OrderProjectionProcessor.cs` |
| 2 | `SagaTimeoutSweeper` cancels backordered orders after 5s, defeating backorders and leaking stock | **P0** | `Orders.Worker/SagaTimeoutSweeper.cs`, `SagaOrchestrationStore.ClaimCandidatesSql` |
| 3 | The in-progress `OrderStatusChanged` covers only the API path; the saga path and returns still emit nothing | **P1** | `Orders.Worker/OrderStatusStore.cs`, `Orders.Infrastructure/Persistence/EfOrderReturnRepository.cs` |
| 4 | The projection applies status writes with no ordering guard, and the outbox does not preserve per-aggregate order across retries | **P1** | `Orders.Worker/OrderProjectionStore.cs:22`, `BuildingBlocks.Persistence/OutboxPublisher.cs:90` |
| 5 | Two divergent implementations of "transition an order", with different side effects | **P1** | `Orders.Worker/OrderStatusStore.cs` vs `Orders.Infrastructure/Persistence/EfOrderStatusRepository.cs` |
| 6 | Concurrent returns over-refund — `returned_quantity` has no concurrency guard | **P1** | `Orders.Domain/OrderLine.cs:58`, `EfOrderReturnRepository.SaveReturnAsync` |
| 7 | `POST /orders/{id}/fulfillment` accepts `Returned`, `Backordered` and `Confirmed`, bypassing the aggregates that own them | **P2** | `Orders.Api/Endpoints/FulfillmentEndpoints.cs:75` |
| 8 | A refund that cannot apply is dropped silently by the saga | **P2** | `Orders.Worker/OrderSagaReplyConsumer.cs:425` |
| 9 | Backorder-fill racing a timeout release leaks stock permanently | **P2** | `Inventory.Service/InventoryReservationMessageProcessor.Backorders.cs:38` |
| 10 | Checkout hard-fails on any catalog price movement, with no re-price path | **P2** | `Storefront.Service/StorefrontCheckoutPolicy.cs:18`, `Cart.Service/Domain/CartCrdtState.cs:38` |
| 11 | The outbox publisher holds a Postgres transaction open across every Kafka round trip and counts the backlog every tick | **P3** | `BuildingBlocks.Persistence/OutboxPublisher.cs:89` |
| 12 | Anti-entropy checks three cross-service invariants but not the one that is actually broken | **P3** | `Orders.Worker/AntiEntropySweeper.cs` |
| 13 | Comment/config drift about the default saga mode | **P3** | `Orders.Worker/Program.cs:209`, `docs/saga/milestone-75-saga-mode-both-by-default.md` |

---

## 1. The read model and the event-store timeline are dead (P0)

`Saga:Mode` is set to `Orchestration` in every deployed configuration
(`compose/compose.yaml:819` and `:879`, `kubernetes/base/orders-worker.yaml:107`,
`kubernetes/base/payments-service.yaml:67`), and that is also the code default
(`Orders.Worker/Program.cs:210`, `Payments.Service/Program.cs:150`).

In `Orchestration` mode, `PaymentMessageProcessor` is not registered
(`Payments.Service/Program.cs:152`). It is the **only** producer of
`PaymentDecided` anywhere in the repository — the orchestrated processor
publishes `PaymentDecisionReplied` instead. So `payments.result.v1` is never
written to.

Both projections nevertheless depend on it for every status other than `Created`:

- `OrderProjectionProcessor.ProcessPaymentDecidedAsync` is what writes
  `Confirmed`/`Cancelled` into `order_summaries`.
- `OrderEventStoreProjector.AppendPaymentDecidedAsync` is what appends
  `OrderConfirmed`/`OrderCancelled` to `order_events`.

The saga's own status writes go through `OrderStatusStore`, which is raw SQL
against `orders` and publishes nothing.

**Consequence.** `GET /orders/summary` reports every order as `Created`
indefinitely, and its `?status=` filter is meaningless. `GET /orders/{id}/history`
shows a single `OrderCreated` entry for the entire life of an order.
`GET /orders/{id}` is unaffected — it reads `orders` directly — which is why
this has stayed invisible.

This is the root cause the in-progress `OrderStatusChanged` work is reaching
for, but it is being fixed from the wrong end: the API path
(`EfOrderStatusRepository`) is the one path that was *not* the main producer of
status changes.

**Fix.** Make `OrderStatusChanged` the single status-change event, emitted from
*both* transition implementations, and stop the projections from depending on
`PaymentDecided` for status at all.

1. Add an outbox write to `OrderStatusStore.ApplySideEffectsAsync`, in the same
   transaction as the CAS, mirroring what the working tree already added to
   `EfOrderStatusRepository.TryTransitionAsync`. `OrderStatusStore` uses raw
   Npgsql, so this is an `INSERT INTO outbox_messages`, not `dbContext.Add` —
   both write the same table in the same database.
2. Keep `ProcessPaymentDecidedAsync` and `AppendPaymentDecidedAsync` registered
   (choreography mode still needs them) but make them tolerate being the second
   writer, per finding 4.
3. Extend the `OrderEventStoreProjector` naming so `OrderConfirmed` /
   `OrderCancelled` are produced from `OrderStatusChanged` too, and confirm
   `GetOrderHistoryHandler.Fold` still folds them — the working tree already
   added the other seven cases.
4. Add an integration test that drives a full orchestrated order to `Confirmed`
   and asserts on `order_summaries.status` and on the `order_events` timeline,
   not just on `orders.status`. The absence of such a test is why this survived.

---

## 2. Backordered orders are cancelled after five seconds (P0)

`docs/domain/milestone-74-backorders.md` states the design explicitly: on a
backorder the saga row "is left exactly where it is, still parked at the
`ReserveInventory` step. Nothing completes it, nothing cancels it." Giving up is
`BackorderTimeoutSweeper`'s job, after `Backorder:TimeoutMinutes` — default
**120 minutes**.

`SagaTimeoutSweeper` does not know about any of this.
`SagaOrchestrationStore.ClaimCandidatesSql` selects on `requested_at <= @cutoff`
alone, with no filter on step and no notion of a parked saga, and
`SagaOrchestration:TimeoutSeconds` is **5** (`Orders.Worker/appsettings.json:47`).
The backordered saga row's `requested_at` is never touched after the initial
reservation request.

**Consequence.** Roughly five seconds after creation, every backordered order is
claimed by the Orders-side sweeper, which:

- queues a release for every line (`CreateTimeoutCommands`, which fires for
  `ReserveInventory`),
- deletes the saga row and its lines,
- cancels the order (`ResolveAsync`'s `default` branch).

The backorder row in Inventory survives — nothing told Inventory. When stock
eventually arrives, `ReleaseBackordersAsync` reserves against that same
`reservationId` and publishes `InventoryReservationReplied(Reserved: true)` to a
saga row that no longer exists (`UnknownReply`). **Those units are now reserved
and nothing will ever release them.** The entire backorder feature — the FIFO
queue, the multi-warehouse allocation behind it, `OrderStatuses.Backordered` —
is unreachable in practice.

`AntiEntropySweeper.CheckBackordersBelongToWaitingOrdersAsync` should be firing
`backorder_on_dead_order` continuously today; that counter is the fastest way to
confirm this before changing anything.

**Fix.**

1. Add a `parked_at timestamptz null` column (or a `backordered boolean`) to
   `saga_orchestration_states`, set when `HandleReservationRepliedAsync` takes
   the backordered branch.
2. Exclude parked rows from `ClaimCandidatesSql`. Prefer this over "bump
   `requested_at`": bumping makes the row indistinguishable from a fresh
   request and would reset the step timeout on every unrelated event.
3. Give `BackorderTimeoutSweeper` the job of un-parking: when it expires a
   backorder it already replies `Reserved: false, Backordered: false`, which the
   saga treats as a permanent refusal — the saga row must still be there to
   receive it.
4. Make `SagaTimeoutSweeper` tolerate the reply for a released backorder
   arriving after the parked window: today `RecordLineOutcomeAsync` returning
   `null` is logged as `UnknownReply` and dropped. Emit a metric there
   (`saga.orphaned_reply`) — a dropped `Reserved: true` reply is always stock
   nobody will release.
5. Add an integration test: reserve into a backorder, advance a fake clock past
   `SagaOrchestration:TimeoutSeconds` but not past `Backorder:TimeoutMinutes`,
   assert the order is still `Backordered` and the saga row still exists.

---

## 3. `OrderStatusChanged` is emitted from one of the four places that change status (P1)

With the working tree as it stands, `OrderStatusChanged` is written by
`EfOrderStatusRepository.TryTransitionAsync` only. Status also changes in:

- `Orders.Worker/OrderStatusStore.TransitionAsync` — every saga-driven
  `Confirmed`, `Cancelled`, `Backordered` and `FulfillmentHold`. This is the
  high-volume path.
- `EfOrderReturnRepository.SaveReturnAsync` — the `Delivered → Returned` write,
  a bare `UPDATE orders SET status = ...`.
- `SagaTimeoutSweeper` / `OrderSagaReplyConsumer`, indirectly through
  `OrderStatusStore`.

`GetOrderHistoryHandler`'s new fold has a case for `OrderReturned`, but nothing
produces it. That case is currently dead code.

**Fix.** Route every status write through one place (finding 5), and emit the
event there. Until that consolidation lands, add the outbox write to
`OrderStatusStore` and to `SaveReturnAsync`'s `markOrderReturned` branch, both
inside their existing transactions.

---

## 4. Status writes to the projection are not ordered (P1)

`OrderProjectionStore.UpsertDecisionSql` is an unconditional
`ON CONFLICT DO UPDATE SET status = EXCLUDED.status`. `decided_at` is stored but
never compared. The working tree's `ProcessOrderStatusChangedAsync` reuses it
verbatim.

Two independent sources of reordering:

- **Across topics.** `orders.created.v1`, `payments.result.v1` and
  `orders.status-changed.v1` are three topics on one consumer. Nothing orders a
  message on one against a message on another, even for the same order id.
- **Within the outbox.** `OutboxPublisher.ProcessBatchAsync` orders by
  `occurred_at`, but a message that fails gets `next_attempt_at` pushed into the
  future by `MarkFailed` while later messages for the same aggregate publish
  immediately. A single transient produce failure on `Shipped` is enough for
  `Delivered` to be projected first and then permanently overwritten by
  `Shipped`.

The inbox dedup does not help — these are distinct events, not redeliveries.

**Fix.**

1. Guard the upsert on time:
   `WHERE order_summaries.decided_at IS NULL OR EXCLUDED.decided_at >= order_summaries.decided_at`.
   `OccurredAt` is assigned in the same transaction as the CAS, so it is a
   faithful order for the write model.
2. Prefer a monotonic sequence over a timestamp if clock skew across replicas is
   a concern: a `status_seq bigserial` on `outbox_messages`, carried on the
   event, is strictly stronger and removes the tie-breaking question entirely.
3. In `OutboxPublisher`, stop publishing past a failed row for the same
   aggregate. The minimum change is to add `order_id`/aggregate-id to
   `outbox_messages` and skip rows whose aggregate has an earlier unpublished
   row. Alternatively, keep it simple and rely on (1)/(2) — but then document
   that the outbox is explicitly unordered, because three separate comments in
   the codebase currently imply it is not.

---

## 5. Two implementations of one business rule (P1)

`OrderStatuses` centralizes *legality* — and its comment says the settlement
policy is centralized "because both Orders.Worker (saga-driven) and Orders.Api
(operator-driven) act on it, and duplicating the policy is what would let their
different mechanisms drift apart." The transition *side effects* were then
duplicated anyway:

| Side effect | `OrderStatusStore` (Worker) | `EfOrderStatusRepository` (Api) |
|---|---|---|
| Coupon confirmed on `Confirmed` | yes | **no** |
| Coupon released on `Cancelled` | yes | yes |
| Customer tier recorded on `Confirmed` | yes | **no** |
| Capture command on `Shipped` | yes | yes |
| Cancellation command on `Cancelled` | yes | yes |
| Inventory compensation on `Cancelled` | **no** | yes |
| In-flight saga flagged as cancelled | **no** | yes |
| `OrderStatusChanged` emitted | **no** | yes (working tree) |

The drift is reachable. `PredecessorsOf(Confirmed)` is `[Created, Backordered]`
and `FulfillmentEndpoints` accepts any `TransitionableTargets` value, so an
admin moving an order to `Confirmed` goes through the API path and **silently
skips the coupon confirmation and the loyalty-tier update**. Conversely, a
saga-driven `Cancelled` skips the inventory compensation that the API path
performs.

**Fix.** Collapse to one implementation. The API's version is the more complete
one and already lives behind `IOrderStatusRepository`; the cleanest shape is:

1. Move the union of side effects into a single `OrderTransitionPolicy` in
   `Orders.Application` (or `BuildingBlocks`), expressed as "given target status
   and previous status, produce this list of outbox commands and local writes".
2. Have both `OrderStatusStore` and `EfOrderStatusRepository` become thin
   executors of that list, differing only in their data access (raw Npgsql vs
   EF) — which is the only difference that was ever justified.
3. Add an architecture test asserting there is exactly one type performing the
   `orders.status` CAS, so a third one cannot appear.

---

## 6. Concurrent returns over-refund (P1)

`ReturnOrderHandler` reads the order via `FindForReturnAsync` (tracked, outside
any transaction), calls `Order.TryReturn` which validates against
`ReturnableQuantity` and mutates via `OrderLine.RecordReturn` (`ReturnedQuantity += quantity`),
then `SaveReturnAsync` opens a transaction and saves.

`returned_quantity` is mapped as a plain required `int`
(`OrdersDbContext.cs:124`) with no concurrency token anywhere in the model — I
found no `IsConcurrencyToken`, `RowVersion` or `xmin` mapping in any of the three
DbContexts.

**Consequence.** Two concurrent full-quantity return requests both read
`ReturnedQuantity = 0`, both pass validation, both write `ReturnedQuantity = N`,
and both queue a `PaymentRefundRequested`. The refund inbox dedups on `ReturnId`,
which differs between the two returns, so nothing downstream stops it.
`Payment.TryRefund`'s cumulative `RefundableAmount` guard is the only backstop —
it caps the total at the payment amount, which prevents refunding *more than was
charged*, but the second return is still accepted, still marks units returned
that were already returned, and still restocks them a second time in Inventory.

The comment on the `markOrderReturned` update ("two returns landing at once must
not both believe they were the one that completed the order") shows the race was
considered for the *status* and not for the *quantities*.

**Fix.**

1. Map `xmin` as a concurrency token on `OrderLine`
   (`.Property<uint>("xmin").IsRowVersion().IsConcurrencyToken()`), and let
   `DbUpdateConcurrencyException` surface as a 409 from the return endpoint.
2. Or, if a retry loop is preferred over a client-visible conflict, do the read
   inside `SaveReturnAsync`'s transaction with `SELECT ... FOR UPDATE` on the
   order's lines, and move `TryReturn` inside it.
3. Add a concurrency integration test — two simultaneous returns of the same
   line, asserting exactly one succeeds.

---

## 7. The fulfilment endpoint can reach states it does not own (P2)

`FulfillmentEndpoints` accepts any value in `OrderStatuses.TransitionableTargets`,
which includes `Returned`, `Backordered` and `Confirmed`.

- `Returned` moves `Delivered → Returned` with **no `OrderReturn` row, no
  refund, no restock, and `returned_quantity` untouched**. The return aggregate,
  the refund arithmetic and the shipping-refund policy are all bypassed.
- `Backordered` moves `Created → Backordered` with no backorder row in
  Inventory, so nothing will ever release it. The anti-entropy backorder check
  cannot catch this: it scans backorders that exist, and there is none.
- `Confirmed` skips reservation and payment entirely (and skips the coupon/tier
  side effects, per finding 5).

**Fix.** Split `TransitionableTargets` into the set an external fulfilment actor
may drive (`Picking`, `Shipped`, `Delivered`, `FulfillmentHold`, `Cancelled`)
and the set only an aggregate or the saga may reach (`Confirmed`, `Backordered`,
`Returned`). Reject the latter from this endpoint with the existing 422, and
keep the full set for `PredecessorsOf`, which is about legality, not authority.

---

## 8. A refund that cannot apply is dropped silently (P2)

`PaymentSettlementProcessor` is careful here: when a settlement cannot apply and
it is not a redelivery, it publishes `PaymentSettlementReplied` carrying the
payment's actual state, with an explicit comment that this "must never pass
silently".

`OrderSagaReplyConsumer.HandleSettlementRepliedAsync` then returns immediately
unless `reply.State == PaymentStates.Expired`.

**Consequence.** A return whose payment is `Authorized` (goods shipped, capture
never landed), `Voided` or `Declined` produces a refund the saga never hears
about. The customer's goods are back in stock, the return is recorded as
accepted, and no money moves. The producing side did its job; the consuming side
throws the reply away.

**Fix.** Handle every non-success state, not just `Expired`. `Expired` keeps its
`FulfillmentHold` treatment; the others need at minimum a counter
(`OrdersTelemetry.RecordSettlementReconciliationUnresolved` already exists and
takes a reason string) and a warning that names the return. A dedicated
`RefundFailed` operational state is the fuller answer, but the counter and log
close the silence.

---

## 9. Backorder fill racing a timeout release leaks stock permanently (P2)

Independent of finding 2 (and still live once finding 2 is fixed, because
`BackorderTimeoutSweeper` and a saga-side release can still interleave):

1. A release for `reservationId` R arrives while R is still only a backorder.
   `ProcessSettlementAsync` claims R in `inbox_messages` under the
   `...-release` consumer, finds no allocation, and replies `succeeded: false`.
2. Stock arrives. `ReleaseBackordersAsync` reserves against that same R and
   creates a real allocation.
3. Any subsequent release for R is dropped as an inbox duplicate. The
   allocation is never settled.

**Fix.** In `ReleaseBackordersAsync`, before reserving, check whether a release
or cancellation for that reservation has already been recorded (the inbox row
under `{ConsumerGroup}-release` is the cheapest signal, or a `cancelled_at`
column on `backorders`). If so, drop the backorder instead of filling it.

---

## 10. Checkout hard-fails on any catalog price movement (P2)

`StorefrontCheckoutPolicy.BuildOrderRequest` sends
`ExpectedSubtotal = cart.Items.Sum(UnitPrice * Quantity)` using the cart's
snapshotted unit prices. `CartItemMetadata` is snapshotted once at first add and
deliberately never merged or refreshed (`CartCrdtState.Increase`, `Merge`).
`CreateOrderHandler` compares that against the freshly-priced subtotal and
returns `PriceMismatch` on any difference.

**Consequence.** Once a catalog price moves, that cart can never check out. The
shopper has to remove and re-add every affected line — there is no re-price, no
"price changed, confirm?" flow, and the error does not say which SKU moved.

**Fix.** Return the per-SKU deltas in the `PriceMismatch` response, add a
`GET /api/storefront/cart/repriced` (or refresh metadata on read) so the
storefront can show the change, and let the shopper re-submit with the new
expected subtotal. The server-side guard itself is correct and should stay.

---

## 11. The outbox publisher holds a transaction open across Kafka round trips (P3)

`OutboxPublisher.ProcessBatchAsync` opens a Postgres transaction, selects up to
`BatchSize` rows `FOR UPDATE SKIP LOCKED`, then awaits a Kafka produce for each
row **sequentially, inside that transaction**, before a single
`SaveChangesAsync`. A slow broker holds row locks and an open transaction for
the whole batch.

It also runs `COUNT(*) WHERE processed_at IS NULL` on every tick (default poll
250ms in the saga publisher) purely to feed a gauge.

**Fix.** Publish outside the transaction and mark published in a short second
transaction (at-least-once is already the contract, and every consumer already
dedups on an inbox). Move the pending-count to a sampled interval rather than
every batch.

---

## 12. Anti-entropy does not check the invariant that is broken (P3)

`AntiEntropySweeper` checks three genuine cross-service invariants and the
design note ("never auto-corrected") is right. But all three compare Orders
against *another service*. The divergence that is actually live today — finding
1, `orders.status` versus `order_summaries.status`, both inside Orders' own
database — is not checked at all.

**Fix.** Add a fourth check comparing `orders.status` to
`order_summaries.status` for orders older than a projection-lag threshold. It is
a single join, needs no HTTP call, and would have surfaced finding 1 the day it
appeared.

---

## 13. Comment and configuration drift (P3)

- `Orders.Worker/Program.cs:209` says "Choreography is the default"; the line
  below it reads `SagaMode.Orchestration`.
- `docs/saga/milestone-75-saga-mode-both-by-default.md` is titled
  "`Saga:Mode=Both` Is the Default Now" and states the manifests were changed to
  `Both`; every manifest and both Compose services now say `Orchestration`, and
  the code default is `Orchestration`. The change happened, was later reverted
  or superseded, and the milestone document was not updated.

**Fix.** Correct the comment, and add a short "superseded by" note to M75 naming
the milestone that moved the default to `Orchestration`.

---

## Suggested sequencing

**Status: implemented 2026-08-14**, findings 1–9 and 11–13 (12 of 13; finding
10 deliberately deferred — see its note below). Every touched project builds
clean (0 warnings, 0 errors) across the whole solution, and every unit,
architecture and contract test suite passes (Orders.UnitTests 256/256,
Payments.UnitTests 22/22, Inventory.UnitTests 20/20, Cart.UnitTests 26/26,
Catalog.UnitTests 26/26, Storefront.UnitTests 23/23, Orders.ArchitectureTests
84/84, Services.ArchitectureTests 89/89, Orders.ContractTests 2/2 + 1 skipped).
Integration tests that need Testcontainers (Postgres/Redpanda) were written or
extended for every finding below but could not be *run* in the environment
this implementation pass had available — no Docker daemon was reachable there.
Run them (`dotnet test` per test project, Docker required) before merging.

**Phase 1 — stop the bleeding (P0).**
Finding 2 first (it silently destroys orders and leaks stock right now), then
finding 1.

- [x] 2a. `parked_at` column + migration on `saga_orchestration_states`
      (`20260814200313_AddSagaOrchestrationParkedAt`)
- [x] 2b. Set it in `HandleReservationRepliedAsync`'s backordered branch
      (`SagaOrchestrationStore.MarkParkedAsync`)
- [x] 2c. Exclude parked rows from `ClaimCandidatesSql`
- [x] 2d. Metric on the dropped-reply path in `RecordLineOutcomeAsync`
      (`OrdersTelemetry.RecordOrphanedSagaReply`, all three reply handlers)
- [x] 2e. Integration test: `SagaTimeoutSweeperTests.ATimeoutAtReserveInventoryDoesNotCancelAParkedBackorderedOrder`
- [x] 3a. Outbox write in `OrderStatusStore.ApplySideEffectsAsync`
- [x] 3b. Outbox write in `EfOrderReturnRepository.SaveReturnAsync`
- [x] 1d. `OrderStatusChangedProjectionTests.AnOrchestratedConfirmationReachesBothTheReadModelAndTheEventStore`
      — drives `OrderStatusStore.TryConfirmAsync` for real, reads the actual
      queued outbox payload, and feeds it to both `OrderProjectionProcessor`
      and `OrderEventStoreProjector`, asserting `order_summaries.status` and
      `order_events` both land on `Confirmed`/`OrderConfirmed`.

**Phase 2 — correctness under concurrency (P1).**

- [x] 4a. Time/sequence guard on `UpsertDecisionSql` (`decided_at`-gated `ON CONFLICT ... WHERE`)
- [x] 4b. Documented the outbox's per-aggregate ordering contract on
      `OutboxPublisher.ProcessBatchAsync` (at-least-once, no per-aggregate
      order across retries) rather than building aggregate-id tracking —
      the read-side guard in 4a is what actually needs to hold, and does.
- [x] 6a. `xmin` concurrency token on `OrderLine` + `OrderReturnConflictException` → 409
- [x] 6b. `ConcurrentReturnTests.TwoConcurrentFullReturnsOfTheSameLineOnlyOneSucceeds`
      — two separate `OrdersDbContext`s both read the same Delivered order
      before either writes (matching two concurrent requests' repositories),
      both pass domain validation, the first `SaveReturnAsync` commits, and
      the second is asserted to throw `OrderReturnConflictException` — the
      sequencing (read both, save first, then second) reproduces the race
      deterministically rather than depending on real thread timing.
- [x] 5a. Fixed directly rather than via a new `OrderTransitionPolicy` type:
      `EfOrderStatusRepository` now also confirms the coupon and records the
      loyalty tier on `Confirmed`, matching `OrderStatusStore`. The other two
      asymmetries this finding flagged (inventory compensation, in-flight-saga
      flagging) turned out to be correct as-is on investigation, not drift —
      each path already handles what it alone is responsible for; see the
      finding text above, now stale on this point, for the original read.
- [ ] 5b. Architecture test: "exactly one type performs the status CAS" — not
      added; given 5a's finding above, the two CAS implementations are
      intentionally separate (different callers, different knowledge), so
      this test as originally specified would be false. No replacement test
      was added in its place.

**Phase 3 — closing the remaining loose ends (P2).**

- [x] 7. Split into `OrderStatuses.FulfillmentDrivableTargets`; `FulfillmentEndpoints` rejects the rest with 422
- [x] 8. `PaymentSettlementReplied.RequiresReconciliation` replaces the `State == Expired` special case; every producer updated
- [x] 9. `SkuAdvisoryLock` (extracted, shared) now also guards `BackorderTimeoutSweeper`, closing the race against `ReleaseBackordersAsync`
- [x] 10. Implemented, fully server-side, no frontend change required:
      `Cart.Service` gained `CartCrdtState.RefreshMetadata` /
      `CartStore.RefreshItemPriceAsync` / `POST /carts/me/items/{sku}/refresh-price`
      — a targeted price-snapshot refresh that leaves CRDT quantity state
      untouched (previously the only way to update a line's price was
      DELETE-then-PUT). `Storefront.Service`'s `CheckoutAsync` now reacts to
      Orders.Api's 409 "Price Changed" by refreshing every cart line's price,
      re-pricing the order request, and retrying `POST /orders` exactly once;
      any other 409 (coupon, idempotency conflict) is untouched, and a reprice
      that can't complete (Cart.Service down, a vanished SKU) relays the
      original conflict unchanged rather than guessing. Covered by unit tests
      in both services (`CartCrdtStateTests`, `CheckoutEndpointTests`).

**Phase 4 — hygiene (P3).**

- [x] 11. `OutboxPublisher` now claims a batch (short transaction, `NextAttemptAt` push) and publishes to Kafka outside any transaction; pending-gauge sampled via `PendingSampleIntervalSeconds`
- [x] 12. Fourth anti-entropy check added: `CheckWriteModelMatchesReadModelAsync` / `AntiEntropyChecks.WriteModelDivergesFromReadModel`
- [x] 13. Fixed the `Program.cs` comment and added a superseded-by note to the M75 doc, citing commit `cfe528f` as where the deployed default actually moved to `Orchestration`

---

## What is genuinely solid

Worth recording, because the plan above is one-sided by construction:

- **`Payments.Service` settlement.** `Payment.TryCapture` / `TryRefund` /
  `TryCancel` are correctly guarded state transitions with a cumulative refund
  cap, and `PaymentSettlementProcessor`'s `FOR UPDATE` on the primary payment
  serializes capture, refund, cancellation and the expiry sweeper against each
  other across topics. The distinction between "redelivery of an applied
  operation" and "a genuine mismatch" is the right one and is made explicitly.
- **`PaymentDecisionCoordinator`.** The advisory lock plus filtered unique index
  is a correct answer to "two saga modes must not create two settleable
  payments", and it validates the inputs rather than trusting whichever arrived
  first.
- **`Inventory.Service`'s SKU advisory lock.** `pg_advisory_xact_lock` scoped by
  SKU with a dedicated seed, held across reserve/commit/release/restock, is the
  right mechanism for a serialization requirement Kafka partitioning genuinely
  cannot provide, and the reasoning is written down where it is used.
- **Idempotency on create.** Consulting the durable key before any catalog,
  coupon or customer I/O — so a replay returns the original order rather than
  re-pricing it — is the correct ordering, and the request-hash conflict
  detection is the right shape.
- **The inbox pattern's per-consumer keying.** One consumer name per logical
  consumer, with the fresh-id-per-line detail called out in two places after it
  caused a real multi-SKU bug, is applied consistently.
- **`Order.TryReturn`.** Validating every line before mutating any of them, and
  keeping the refund arithmetic (including the tax share and the shipping-refund
  policy) inside the aggregate, is the right boundary.
