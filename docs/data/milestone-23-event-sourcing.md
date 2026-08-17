# Milestone 23 Event Sourcing for the Order Aggregate

## Scope

Every prior milestone treats the `orders` table (and `order_summaries`, Milestone 13's CQRS read model) as the source of truth: current state, overwritten in place on every transition. This milestone adds a genuine event-sourced view alongside it - an append-only `order_events` table plus a new `GetOrderHistory` use case that reconstructs Order state by folding over that log, rather than reading a row. Purely additive, the same principle Milestones 21 and 22 already used: a third, independent consumer group (`orders-event-store`) on the same two topics the choreographed CQRS projector (Milestone 13) already reads, so nothing already validated is touched.

## Design

- **`order_events`** (`OrderEventStoreProjector`, Orders.Worker): one row per domain event - `OrderCreated`, `OrderConfirmed`, `OrderCancelled` - each with an auto-increment `id`, the `order_id` it belongs to, an `event_type` discriminator, a `jsonb` payload, and `occurred_at`. Append-only; nothing is ever updated or deleted.
- **`GetOrderHistoryHandler`** (Orders.Application): the actual event-sourcing logic. Loads every event for an order (optionally bounded by an `asOf` timestamp) and folds them in order into an `OrderSnapshot` - customer, amount, currency, and status, where status is simply whatever the last-seen `OrderCreated`/`OrderConfirmed`/`OrderCancelled` event implies. The fold is the entire "read model"; there is no separate table it's kept in sync with.
- **`GET /orders/{id}/history`**: returns the folded snapshot alongside the full ordered event list - the audit trail is a free byproduct of the fold, not a separately built feature.
- **`?asOf=<timestamp>`**: the temporal query. Passing a past timestamp reconstructs the snapshot as it existed at that instant, by folding only events with `occurred_at <= asOf`. This is the concrete capability current-state storage cannot offer at all: `orders` only ever has *now*.
- **No inbox-based deduplication**, unlike the choreographed CQRS projector - the same deliberate scope boundary Milestone 22 documented for its orchestrator. A redelivered message can append a duplicate event; the fold doesn't currently guard against it either. Left out because the milestone is about proving the event-sourcing read pattern works, not re-deriving exactly-once processing a third time in this codebase.

## What didn't work

**A generated migration used `columns: new[] { "order_id", "id" }` for the composite index, which the CA1861 analyzer rejects.** Every other migration in this codebase already uses collection-expression syntax for column lists (`20260727181822_AddTransactionalOutbox.cs` and later). `dotnet ef migrations add` doesn't know about that local convention and defaults to `new[]`. Fixed by hand-editing to `columns: ["order_id", "id"]`, matching the established pattern - a one-line fix, but worth naming because it will recur on every future migration this way unless the scaffolding itself is changed.

**The event-sourcing feature itself worked on the first deploy. The regression check (`k3s-smoke-test.sh`) did not - and the reason had nothing to do with event sourcing.** After deploying, `k3s-smoke-test.sh` failed three consecutive times with "The worker did not log the final correlation within the timeout," even though every direct measurement said the system was healthy: the exact log line the test was searching for was confirmed present (`kubectl logs` showed `orders-event-store`'s consumer group at zero lag across all six partitions, `orders-worker`'s own consumer group at zero lag, pod CPU at 13m), and a direct, isolated timing check showed the order was actually processed in 22ms - not the multi-second delay a real regression would produce.

The contradiction resolved to a latent bug in the smoke test script itself, `scripts/smoke-test.sh`, exposed - not caused - by this milestone:

```bash
# before
if worker_logs 2>&1 | grep --quiet --fixed-strings "$last_correlation_id"; then
```

runs under the script's `set -euo pipefail`. `grep --quiet` exits the instant it finds a match, without reading the rest of its input - closing the read end of the pipe while `kubectl logs` may still be mid-write. That earns `kubectl logs` a `SIGPIPE`, giving the pipeline an exit code of 141. Under `pipefail`, that 141 makes the whole `if` false, *even though grep found the match*. Reproduced directly:

```
$ kubectl logs -n orders-lab deployment/orders-worker --since=5m 2>&1 \
    | grep --quiet --fixed-strings smoke-20260728T221352Z-6; echo $?
141
```

This race existed before this milestone too, but Milestone 23 made it far more likely to trigger: `orders-worker` now runs four additional hosted services in the same process (`OrderSagaOrchestrator`, `OrderSagaReplyConsumer`, `SagaTimeoutSweeper`, `OrderEventStoreProjector`), each logging its own scoped, verbose JSON line (full trace/span dictionaries) for every order - substantially more log volume per request than when the smoke test was first written, and more chances for `kubectl logs`'s write buffer to still be flushing when `grep -q` closes early. Fixed by capturing the log output into a variable first, so grep operates on an already-complete string instead of a live pipe:

```bash
# after
captured_worker_logs=$(worker_logs 2>&1) || true
if grep --quiet --fixed-strings "$last_correlation_id" <<<"$captured_worker_logs"; then
```

Verified with three consecutive clean runs of `k3s-smoke-test.sh` after the fix, plus the full `dotnet test` suite (24 unit + 7 integration, all passing).

## Results

A fresh order, confirmed, queried through the new endpoint:

```
$ curl -s http://orders-api/orders/3100d200-.../history
{
  "orderId": "3100d200-8b61-4f7e-94e7-cab5697e0f80",
  "snapshot": {
    "customerId": "doc-demo", "amount": 77.00, "currency": "BRL",
    "status": "Confirmed", "createdAt": "2026-07-28T22:18:53.255101+00:00"
  },
  "events": [
    { "id": 41001, "eventType": "OrderCreated",   "occurredAt": "2026-07-28T22:18:53.255101+00:00" },
    { "id": 41002, "eventType": "OrderConfirmed", "occurredAt": "2026-07-28T22:18:53.32647+00:00"  }
  ]
}
```

The same order, queried `asOf` a timestamp between those two events - reconstructing state as it existed 70ms earlier, before confirmation:

```
$ curl -s 'http://orders-api/orders/3100d200-.../history?asOf=2026-07-28T22:18:53.30Z'
{
  "orderId": "3100d200-8b61-4f7e-94e7-cab5697e0f80",
  "snapshot": { "status": "Created", ... },
  "events": [ { "id": 41001, "eventType": "OrderCreated", ... } ]
}
```

Same aggregate, same endpoint, two different answers depending on the boundary asked for - the capability `orders` (a mutable current-state row) cannot express at all.

On first deploy, `OrderEventStoreProjector` had to replay the entire historical `orders.created.v1`/`payments.result.v1` topic history (191K+ orders from Milestone 20's backfill) to build the event log from scratch - confirmed via `kafka-consumer-groups.sh --describe --group orders-event-store` reaching `LAG=0` on all six partitions before the smoke-test investigation above began, ruling out backlog catch-up as a cause of anything downstream.

### Regression check

`dotnet test`: 24 unit + 7 integration, all passing. `k3s-smoke-test.sh`: three consecutive clean runs after the pipefail fix. The choreographed and orchestrated saga paths, both already validated in prior milestones, are untouched by this one.

## Running the experiment

```bash
# Create an order, let it confirm, then inspect its full history
curl -X POST http://<orders-api>/orders -d '{"customerId":"demo","items":[{"sku":"SKU-BOOK-002","quantity":1}]}'
curl http://<orders-api>/orders/<order-id>/history

# Temporal query: state as it existed before confirmation
curl "http://<orders-api>/orders/<order-id>/history?asOf=<timestamp-between-created-and-confirmed>"
```
