# Milestone 12 Payments Service and Choreographed Saga

## Scope

This milestone adds a second, independently deployed service — **Payments.Service** — and turns the lab into a genuine multi-service distributed system: two services, two databases, coordinating purely through events with no synchronous call between them (a **choreographed saga**, not an orchestrator).

Flow:

1. `POST /orders` (Orders.Api) commits the order and an `OrderCreated` Outbox row in one PostgreSQL transaction, exactly as before. The order's initial status remains `Created`.
2. Payments.Service consumes `orders.created.v1` on its own consumer group (`payments-service`), decides **approve or decline** using a deterministic rule (amount ≤ `PaymentDecision:DeclineAmountThreshold`, default 1,000 - *replaced in Milestone 66 by a scored risk policy over the customer's own history*), and commits a `Payment` row + a `PaymentDecided` Outbox row in one transaction against its **own** `payments` database — the same Inbox/Outbox pattern as Orders.Api, just owned by a different service and a different schema.
3. Payments.Service's own Outbox publisher publishes `PaymentDecided` to `payments.result.v1`.
4. A new consumer inside **Orders.Worker** (`PaymentResultConsumer`, its own consumer group `orders-worker-payments-result`) consumes that event and transitions the order: `Created → Confirmed` on approval, `Created → Cancelled` on decline — the compensating action for this saga, since with only two services there's nothing external left to unwind. It then invalidates the Redis cache entry (M9's mechanism, reused as-is).

This replaces Milestone 9's placeholder behavior, where the Worker auto-confirmed every order the moment it received `orders.created.v1`. That was always a stand-in for a real payment decision; this milestone is the payoff.

## Design decisions

- **Database-per-service, same PostgreSQL instance**: `payments` is a separate database (not schema) on the same Postgres container, created idempotently by `scripts/init-payments-db.sh` (`CREATE DATABASE ... WHERE NOT EXISTS`, via `psql`'s `\gexec`). A fully separate Postgres container was judged unnecessary resource overhead for a 16 GB lab server; the important property — no cross-service joins, independent schema evolution — holds either way.
- **One event, one topic**: `PaymentDecided` (topic `payments.result.v1`) carries an `Approved` boolean rather than publishing to two separate `payments.approved.v1`/`payments.declined.v1` topics. Simpler consumer wiring, same saga semantics.
- **`RetryDelayCalculator` promoted to `BuildingBlocks`**: previously private to Orders.Worker; a second service needing the identical backoff logic was the natural point to promote a pure, dependency-free utility to shared code (the "rule of three" moment, pulled forward because the shape was already proven).
- **Payments.Service is EF-Core-first, not raw-SQL-first**: unlike Orders.Worker (which uses raw `NpgsqlDataSource` throughout because it has no other reason to depend on EF Core), Payments.Service's write path is structurally closest to `Orders.Api.CreateAsync` — a single business-logic-driven insert-and-outbox transaction — so it uses `PaymentsDbContext` end to end, including a `ExecuteSqlInterpolatedAsync` `INSERT ... ON CONFLICT DO NOTHING` for the Inbox row (atomic dedup, same semantics as the Worker's raw-SQL `InboxStore`, just issued through the same `DbContext`/transaction as the rest of the write).
- **New consumer, not a bigger `OrderMessageProcessor`**: `PaymentResultConsumer`/`PaymentResultProcessor` are new, separate classes mirroring `OrderCreatedConsumer`/`OrderMessageProcessor`'s structure (retry loop, infra-fault handling from Milestone 10, seek-and-retry semantics, own DLQ). Orders.Worker now runs two independent consumers in the same process — one per topic, one per concern.

## What didn't work (and the real fixes)

Three genuine problems surfaced only by running the saga end-to-end on the live deployment — each is a real lesson, not a formality:

1. **`kafka-init` doesn't rerun just because application code changes.** It's a one-shot Compose container (`restart: "no"`); adding new topic-creation commands to it only takes effect on the *next* `docker compose up`, which nothing in the deploy flow was forcing. Every payment publish failed with `Local: Unknown topic` because `payments.result.v1` and its DLQ topics were never actually created — a silent gap between "I edited the compose file" and "the change is live." **Fixed** by adding a mandatory `docker compose up --detach --wait` as the first step of `scripts/k3s-deploy.sh`, so any pending Compose-side config (new topics, new services) is always applied before application code that depends on it is deployed.
2. **A brand-new consumer group with `AutoOffsetReset.Earliest` replays a topic's entire retained history.** The very first time Payments.Service started, its consumer group had never committed an offset, so it began consuming `orders.created.v1` from the beginning of the 24-hour retention window — tens of thousands of orders created by every earlier milestone's load tests. This is correct Kafka behavior, not a bug, but it meant genuinely fresh test orders sat behind a multi-minute backlog. Worked around for this session by resetting the consumer group to `--to-latest`; documented here because it's a real operational gotcha when introducing a new consumer to an existing high-volume topic without an explicit backfill decision.
3. **The Kafka producer's circuit breaker (Milestone 10) tripped under the backlog-draining load and looked, at first glance, like a stuck/permanent failure.** It wasn't: once the topics existed and the backlog was cleared, a restarted Payments.Service processed new orders correctly within about a second. The lesson: circuit-breaker-open errors need the *underlying* fault checked before assuming the breaker itself is broken — in this case the breaker was doing exactly its job against a real (if temporary and self-inflicted) Kafka production problem.

## Results

### Saga convergence (`scripts/k6-run.sh saga`, 10 VUs, 30 s, alternating above/below the decline threshold)

| Measurement | Result | Acceptance |
| --- | ---: | ---: |
| Successful checks | 100.00% | > 99% |
| Failed HTTP requests | 0.00% | < 1% |
| Saga converged (reached a terminal state) | 100.00% | > 99% |
| Saga correct outcome (Confirmed iff approved) | 100.00% | == 100% |
| Convergence time (order created → terminal status) | avg 753 ms, p99 1,009 ms | — |

### Chaos: killing Payments.Service mid-flight (`scripts/saga-chaos-test.sh 10`)

| Step | Result |
| --- | --- |
| Orders created while Payments.Service was scaled to 0 replicas | 10/10 succeeded (201) |
| Orders remaining in `Created` during the outage | 10/10 (order creation is fully decoupled from payment availability) |
| Orders converged to `Confirmed`/`Cancelled` after scaling back to 1 replica | 10/10 |

No order was ever lost, duplicated, or stuck once the dependency recovered — the same durability story Milestone 8 proved for the Orders/Worker pair now holds across a real service boundary.

## Running the experiments

```bash
cd /srv/local-distributed-lab
scripts/k6-run.sh saga
scripts/saga-chaos-test.sh 10
```
