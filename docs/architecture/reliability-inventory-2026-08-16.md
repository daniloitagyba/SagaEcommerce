# Producer/Consumer Reliability Inventory (2026-08-16)

Roadmap Phase 3, task 1: every real business Kafka topic (Debezium/CDC/schema-
registry infrastructure topics excluded), its producer(s), consumer(s), and
whether it goes through the standard Outbox/Inbox mechanisms. Cart.Service has
no Kafka wiring at all - excluded.

## Topic inventory

| Topic | Producer | Consumer | Outbox? | Inbox dedup? | DLQ |
|---|---|---|---|---|---|
| `orders.created.v1` | Orders.Api | Orders.Worker (4 consumer groups), Payments.Service (choreography) | Yes | Yes, except `OrderSagaOrchestrator` (CAS instead) and `OrderEventStoreProjector` (none, deliberate - see below) | `orders.created.dlq.v1` |
| `orders.status-changed.v1` | Orders.Api | Orders.Worker (`OrderProjectionProcessor`, `OrderEventStoreProjector`) | Yes | Yes / none (same exception as above) | shares projection/event-store DLQs |
| `payments.result.v1` | Payments.Service | Orders.Worker (choreography + projection + event store) | Yes | Yes / Yes / none | `payments.result.dlq.v1` |
| `payments.decision-requested.v1` → `payments.decision-replied.v1` | Orders.Worker saga (non-standard outbox, see below) → Payments.Service | Payments.Service → Orders.Worker | No / Yes | Yes / none | `payments.decision-requested.dlq.v1` |
| `inventory.reservation-requested.v1` → `-replied.v1` | Orders.Worker saga (non-standard outbox) → Inventory.Service | Inventory.Service → Orders.Worker | No / Yes | Yes / none | `inventory.reservation.dlq.v1` |
| `inventory.reservation-commit-requested.v1` → `-replied.v1` | Orders.Worker saga → Inventory.Service | Inventory.Service → Orders.Worker | No / Yes | Yes / none | shared |
| `inventory.reservation-release-requested.v1` → `-replied.v1` | Orders.Worker saga → Inventory.Service | Inventory.Service → Orders.Worker | No / Yes | Yes / none | shared |
| `inventory.restock-requested.v1` | Inventory.Service, Orders.Api (returns), Orders.Worker saga (backorder path) - 3 producers | Inventory.Service | Yes/Yes/No | Yes | shared |
| `inventory.restock-replied.v1` | Inventory.Service | **none - orphan topic, see below** | Yes | n/a | shared |
| `inventory.backorder-cancellation-requested.v1` | Orders.Api | Inventory.Service | Yes | Yes | shared |
| `inventory.replenishment-needed.v1` | Inventory.Service (self-loop) | Inventory.Service | Yes | Yes | shared |
| `payments.capture-requested.v1` / `refund-requested.v1` / `cancellation-requested.v1` | Orders.Api | Payments.Service (`PaymentSettlementProcessor`) | Yes | Yes | `payments.settlement.dlq.v1` |
| `payments.settlement-replied.v1` | Payments.Service | Orders.Worker (orchestration) | Yes | none | `orders.saga.dlq.v1` |

Every `*.dlq.v1` topic terminates in `DlqRedriveTool` (a manual CLI, not one of
the five always-on services) - none has a steady-state consumer, by design.

## Two findings worth carrying forward, not re-discovering later

**`SagaOutboxPublisher` is architecturally an outbox, but not
`BuildingBlocks.Persistence.OutboxMessage`.** Every saga *request*-side
command (`inventory.reservation-requested.v1`,
`inventory.reservation-commit-requested.v1`,
`inventory.reservation-release-requested.v1`,
`payments.decision-requested.v1`) is drained from a separate
`saga_outbox_messages` table via raw Npgsql, not the shared `OutboxPublisher<TDbContext>`
every other producer in this table uses. This is deliberate and already
documented - `docs/roadmap-milestones-91-99.md` explains the split exists
because the saga outbox previously "held no locks across the Kafka round
trip" the way the shared one does, and M91-99 (closed in `77feb39`) hardened
this specific outbox rather than merging it into the shared one. Not a gap;
recorded here so a future pass doesn't flag the inconsistency as new.

**`inventory.restock-replied.v1` is an orphan topic - produced, never
consumed.** Inventory.Service publishes it after every restock decision
(`InventoryReservationMessageProcessor.ProcessRestockAsync`), but no consumer
in any of the five services subscribes to it. Two honest readings: either
this is a genuinely dead signal nothing has ever needed (the return/backorder
flows that trigger a restock don't wait on its outcome, by design), or it's a
gap where a caller *should* be told a restock failed and currently is not.
Given `TryRestockAsync`'s own doc comment says "the mutation succeeds unless
a caller-named warehouse has no stock row for the sku (should not happen in
practice)," a failed restock reply going nowhere is a low-probability, high-
silence combination worth a deliberate decision rather than leaving
unexamined. **Recommendation:** either wire a consumer (Orders.Api, to close
a return's audit trail) or document explicitly that this reply is
intentionally fire-and-forget and drop it to a debug-level log instead of a
published topic.

## Consolidation and concurrency guards (tasks 2-3)

Already enforced, not newly built:

- **No hand-rolled inbox dedup SQL outside `BuildingBlocks.Persistence`** -
  verified via `.githooks/pre-push`'s own check (zero offenders as of this
  commit), the same script `ci.yml` runs on every push.
- **Concurrency/ordering guards** - `SkuAdvisoryLock` (Inventory), CAS-guarded
  status transitions (`OrderStatuses.AllowedPredecessors`), and the coupon/
  campaign atomic-claim pattern (audit-2026-08-16, Phase 4) all predate and
  survive this rebaseline; no new race was found while building this
  inventory.

## Retry budgets and delivery-guarantee documentation (tasks 4, 6)

Already exist: `ResilienceExtensions.cs`'s `PostgresPipeline` (2 retries,
jittered exponential backoff, 2s timeout) and `KafkaProducerPipeline` (1
retry, 3s timeout) sit under Kafka's own poll/session-timeout defaults. The
delivery-guarantee claim in `README.md` was tightened alongside this
inventory to state the three-tier reality explicitly (local atomicity,
at-least-once transport, effectively-once only where durable idempotency
proves it) instead of a single unqualified "at-least-once" bullet.

## What is not re-proven here (task 5)

Restart, duplicate, out-of-order and compensation-failure scenarios are
proven by the integration-test suite (`*IntegrationTests` projects, run with
real Testcontainers in `ci.yml`'s `test` job) and the chaos scripts under
`scripts/chaos/` - this rebaseline did not re-run either against the lab, per
`ENVIRONMENT.md`'s "does not run locally" boundary. Their existence and CI
integration is confirmed; a fresh live run is not claimed.
