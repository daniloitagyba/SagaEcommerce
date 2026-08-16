# Roadmap: Milestones 91-99

A distributed-systems engineering audit of the seven services as of
Milestone 89, and a phased plan to close what it found.

Like `docs/roadmap-milestones-81-90.md`, this document is written *before*
the work rather than after it, and will be superseded by the per-milestone
reports it plans. Milestone 90 (Phase 5 of the previous roadmap,
business-rule depth) is deliberately left unclaimed here.

## Scope, and how this audit differs from the last one

The Milestone 81-90 audit read the system as a **domain**: what the saga
does to money and stock, who is allowed to do it, what the checkout can
reach. Its conclusion was that "the saga knows how to undo itself, nothing
else does," and Phases 1-4 fixed that.

This audit reads the same system as a **distributed system**: what happens
under partition, redelivery, clock skew, leadership handoff, slow brokers
and concurrent replicas. It deliberately does not re-examine business
rules, authorization or pricing.

Read end to end for this pass: `BuildingBlocks.Persistence` (outbox, inbox,
retention), `BuildingBlocks.Messaging` (consumer host, DLQ), the whole of
`Orders.Worker` (orchestrator, reply consumer, saga outbox, both sweepers,
leader election, projections), `Inventory.Service`'s reservation processor
and allocation store, `Payments.Service`'s settlement processor,
`Orders.Api`'s rate limiting and health wiring, `Cart.Service`'s CRDT
store, `Storefront.Service`'s BFF proxy, plus `compose/compose.yaml`'s
topic provisioning and every service's readiness registration.

**The overall verdict is that the mechanisms are right and the *parameters*
and *edges* are wrong.** The transactional outbox, the inbox, the
per-SKU advisory lock, the CAS-guarded status transitions and the
line-level saga state machine are all correctly built and correctly
reasoned about in their own comments. Almost every finding below is one of
three shapes:

1. **The same pattern implemented twice, with only one of them correct.**
   The system has two transactional outboxes; one holds no locks across
   Kafka, the other holds fifty.
2. **A durable claim followed by a non-durable act.** Several loops delete
   or claim state in a committed transaction and then perform the
   consequence outside it, with no way to recover the consequence if the
   process dies in between.
3. **A timeout, batch size or probe tuned against the happy path**, so that
   ordinary retry latency is indistinguishable from failure.

---

## Part 1 - Findings

Severity is "what a real production incident would look like," not
"how hard is it to fix."

### Severity 1: the saga outbox holds Postgres row locks across the Kafka round trip

`apps/src/Orders.Worker/SagaOutboxPublisher.cs:54-98`

`ProcessBatchAsync` opens a transaction (line 58), selects up to
`OutboxBatchSize` rows `FOR UPDATE SKIP LOCKED` (line 68), and then calls
`PublishAsync` for each of them **inside that still-open transaction**
(line 93), committing only after the last one (line 96). `PublishAsync`
awaits `producer.ProduceAsync(...)` wrapped in the Kafka Polly pipeline
(line 122-124).

The shared outbox in `BuildingBlocks` explicitly refuses to do this, and
says why:

> Outside any transaction from here on - a slow or unreachable broker must
> never hold Postgres row locks (or the open transaction they imply) for as
> long as Kafka takes to answer
> — `apps/src/BuildingBlocks.Persistence/OutboxPublisher.cs:108-112`

The two implementations are the same pattern with opposite conclusions,
and the saga one has the worse defaults: `OutboxBatchSize` is 50
(`SagaOrchestrationOptions.cs:52`) against the shared outbox's 5
(`OutboxOptions.cs:7`), and the Kafka pipeline allows 3s timeout + 1 retry
per message (`ResilienceExtensions.cs:43-60`). A broker that is slow rather
than down therefore produces a **single Postgres transaction open for up to
several minutes**, holding 50 row locks, one pooled connection, and — worse
than either — a transaction snapshot that blocks `VACUUM` from reclaiming
dead tuples across the *whole* Orders database for its duration.

This is also why the saga outbox does not scale past one effective
publisher: a second replica's `SKIP LOCKED` will find rows, but the first
replica's long-lived transaction is what actually determines throughput.

**Fix.** Delete `SagaOutboxPublisher`'s bespoke loop and make
`saga_outbox_messages` a second `OutboxPublisher<TDbContext>` target, or —
if the raw-Npgsql style is worth keeping — port the shared publisher's
claim-then-publish-then-mark shape verbatim: claim with a
`next_attempt_at` push in a transaction that commits immediately, publish
outside any transaction, mark processed in a second short transaction. The
`ClaimWindowSeconds` mechanism (`OutboxOptions.cs:22`) already exists and
is already reasoned about; this is reuse, not new design.

### Severity 1: the saga timeout (5s) is shorter than the system's own retry budget

`apps/src/Orders.Worker/SagaOrchestrationOptions.cs:48` sets
`TimeoutSeconds = 5`, swept every second
(`SweepIntervalMilliseconds = 1_000`, line 50).

Add up what one legitimate reservation round trip is allowed to cost:

| Stage | Worst case | Source |
|---|---|---|
| Saga outbox poll | 250 ms | `SagaOrchestrationOptions.cs:54` |
| Kafka produce (3 s timeout + 1 retry) | ~6.1 s | `ResilienceExtensions.cs:43-60` |
| Inventory consumer in-process retries | ~750 ms | `MessageProcessingOptions.cs` + `RetryDelayCalculator` |
| **Inventory infrastructure back-off** | **unbounded, 1 s per cycle** | `KafkaConsumerHost.cs:105-110, 163-166` |
| Reply outbox poll + publish | ~0.5-6 s | `OutboxOptions.cs:9`, `ResilienceExtensions.cs:43-60` |
| Reply outbox claim window (on publisher crash) | 30 s | `OutboxOptions.cs:22` |

The Kafka produce leg alone can exceed the whole timeout. The
infrastructure leg is worse than "slow": an `NpgsqlException` or a
`BrokenCircuitException` inside `Inventory.Service` does not consume a
retry attempt at all — `KafkaConsumerHost.cs:105-110` catches it, waits
1 s, seeks back and re-reads the same message, for as long as the fault
lasts. That is deliberate and correct; it is also exactly the case
`MessageProcessingOptions` exists to absorb, and it costs far more than 5
seconds. `SagaTimeoutSweeper` therefore fires **while the reservation is
still legitimately in flight**. What follows is not benign: the sweeper queues a
release for every line, deletes the saga row, and cancels the order
(`SagaTimeoutSweeper.cs:74-121`). Inventory then processes the *original*
reserve request, creates a real allocation, and publishes a reply that
`OrderSagaReplyConsumer.HandleReservationRepliedAsync` can only record as
an orphan (`OrderSagaReplyConsumer.cs:74-82`) — stock reserved against a
cancelled order that nothing will ever release, which is exactly the
condition `AntiEntropySweeper`'s committed-inventory check exists to
report after the fact.

The `OrphanedSagaRepliesHigh` alert already in
`observability/prometheus/rules/` is, in effect, an alarm for this
misconfiguration.

**Fix.** Set the saga timeout from a measured p99.9 of the reservation and
decision round trips (a k6 run under Toxiproxy latency already exists to
produce that number), with a floor of "the sum of the table above" — on the
order of 60-120 s, not 5. Keep the 5 s value only as an integration-test
override, where it is genuinely useful, and add a `ValidateOnStart` rule
asserting `TimeoutSeconds` exceeds
`MaximumAttempts * MaximumRetryDelayMilliseconds / 1000` so the two can
never drift apart silently again.

### Severity 1: durable claim, non-durable act — a crash loses the order's resolution

`apps/src/Orders.Worker/SagaTimeoutSweeper.cs:74-87` and
`apps/src/Orders.Worker/SagaOrchestrationStore.Timeout.cs:141-166`

`ClaimTimedOutAndQueueAsync` does the right thing for *stock*: the release
commands are enqueued to the saga outbox in the same transaction that
deletes the saga rows (line 152-163). It does not do the right thing for
the *order*: `ResolveAsync` — which is what actually cancels or confirms
the order — runs in the `foreach` **after** that transaction has committed
(`SagaTimeoutSweeper.cs:80-84`).

If the process dies, is descheduled past its lease, or simply loses
leadership between the commit and the loop, the saga row is gone forever
and the order is left in `Created` (or `Backordered`) with nothing left in
the system that knows to move it. It cannot time out again, because the row
that made it timeout-eligible was deleted.

The same shape appears in `OrderSagaReplyConsumer`: every handler calls
`store.TryComplete…` (which commits) and then `orderStatusStore.TryCancel…`
/ `TryConfirm…` / `cacheInvalidator.InvalidateAsync` afterwards — e.g.
`OrderSagaReplyConsumer.cs:94-111` and `290-352`. There the Kafka offset is
not committed until the whole handler returns, so a redelivery re-runs the
handler; but the second run finds the saga row already gone and takes the
`completed is null` branch (line 314), which logs `UnknownReply` and
returns without ever performing the status transition. **The redelivery
that was supposed to make this recoverable is exactly what makes it
unrecoverable.**

**Fix.** Two changes, in order:

1. Make the status transition part of the same transaction as the saga-row
   deletion. `OrderStatusStore` already writes through raw Npgsql against
   the same database and already accepts an externally-owned
   `NpgsqlConnection`/`NpgsqlTransaction` in `ApplySideEffectsAsync`'s
   collaborators (`OrderStatusStore.cs:155-245`) — the seam exists.
2. Add a reconciliation for the class of failure that survives (1): an
   anti-entropy check for "order in a non-terminal status, older than the
   saga timeout, with no `saga_orchestration_states` row." That is the one
   invariant the sweep does not currently check, and it is the direct
   observable of this bug.

### Severity 2: the read-model ordering guard trusts physical clocks across services

`apps/src/Orders.Worker/OrderProjectionStore.cs:34-42`

`UpsertDecisionSql`'s `WHERE … EXCLUDED.decided_at >= order_summaries.decided_at`
is a last-write-wins guard keyed on a **wall-clock timestamp produced by
whichever service emitted the event**: `Orders.Api`, `Orders.Worker`,
`Payments.Service` and `Inventory.Service` each stamp their own
`DateTimeOffset.UtcNow`/`TimeProvider.GetUtcNow()` before the event is
serialized.

The comment correctly identifies the hazard it is defending against
(out-of-order redelivery) but the defence assumes a global clock. With NTP
skew of even tens of milliseconds between pods — routine, and much larger
during a clock step or a VM migration — a genuinely newer status carrying a
slightly earlier `decided_at` is silently dropped, permanently, with no log
line and no metric. The read model then diverges from the write model,
which `AntiEntropySweeper.CheckWriteModelMatchesReadModelAsync` will
eventually report (`AntiEntropySweeper.cs:243-278`) but never explain.

**Fix.** Give each order a monotonic, single-writer sequence and order on
that instead. The event store already appends per-order events
(`OrderEventStoreAppender`); a per-order `version` column on
`order_events`, carried on `OrderStatusChanged` and compared in the guard,
turns a clock comparison into a causality comparison. Keep `decided_at` for
display. Where a sequence genuinely is not available, prefer status-graph
precedence (`OrderStatuses.PredecessorsOf`) over timestamps — the
transition table already encodes which status is "later."

### Severity 2: readiness is gated on dependencies the code deliberately treats as optional

`apps/src/Orders.Api/Program.cs:126-129` registers `postgres`, `kafka` and
`redis` all under the `ready` tag, and `/health/ready` fails if any is
unhealthy (line 163-166).

But the code's own design says Redis is optional: `RedisOrderCache` fails
open, `RedisSlidingWindowRateLimiter.TryAcquireAsync` explicitly returns
`Allowed: true` on any infrastructure fault and increments a
`RateLimitingFailedOpen` counter (`RedisSlidingWindowRateLimiter.cs:69-74`).
Kafka is optional too: `POST /orders` writes to the outbox inside the order
transaction; a broker outage delays delivery and changes nothing about the
request's success.

So a Redis blip takes **every** `Orders.Api` replica `NotReady`,
Kubernetes removes them all from the Service endpoints, and the API is
100% unavailable — for a dependency the application layer was explicitly
built to survive. This is a self-inflicted cascading failure, and it is
worse than the failure it is reporting.

The same registration exists in `Payments.Service:220-222`,
`Inventory.Service:240-242` and `Catalog.Service:67-68`. For the two
consumer services the consequence is different but still bad: going
`NotReady` does not stop Kafka consumption, it only removes them from the
Service — which is precisely how `AntiEntropySweeper`'s HTTP reads reach
them (`Orders.Worker/Program.cs:154-163`), so the reconciliation loop goes
blind exactly during the incident it exists to observe.

**Fix.** Readiness should contain only what the service cannot serve a
single request without: Postgres for Orders/Payments/Inventory, Mongo for
Catalog, Redis for Cart (where it genuinely *is* the system of record —
`CartStore.cs:17-30`). Move Kafka and Orders' Redis to a third,
non-gating `/health/dependencies` endpoint scraped for alerting. Also
replace `KafkaHealthCheck`'s `Task.Run` around a blocking 3-second
`GetMetadata` (`KafkaHealthCheck.cs:11-18`) — that burns a thread-pool
thread per probe per pod on the readiness path.

### Severity 2: neither outbox can ever give up, and the DLQ expires in 24 hours

Two halves of one gap.

**No poison-message ceiling.** `OutboxMessage.MarkFailed`
(`BuildingBlocks.Contracts/OutboxMessage.cs:74-84`) increments
`AttemptCount` and pushes `NextAttemptAt` out with capped exponential
back-off — but nothing ever reads `AttemptCount` to stop. A row whose
payload can never be published (an event type the dispatcher does not
recognize, a serialization change, a message exceeding
`message.max.bytes`) is retried every 60 seconds forever, indefinitely
inflating the `outbox_pending` gauge that `OutboxBacklogGrowing` alerts on,
and permanently exempting itself from `RetentionSweeper` (which only
deletes rows with a non-null `processed_at` —
`Orders.Worker/Program.cs:104-108`). `SagaOutboxPublisher.cs:135-156` has
the identical hole.

**A DLQ with a one-day TTL.** Every dead-letter topic is created with
`--partitions 1 --replication-factor 1 --config retention.ms=86400000`
(`compose/compose.yaml:419-443`). `DlqRedriveTool` is a manual CLI
(`DlqRedriveTool/Program.cs:10-16`), and `DeadLetterMessagesDetected` is
the only thing that will tell an operator to run it. A poison message that
arrives on a Friday is gone by Monday — the evidence deletes itself before
anyone reads the alert.

**Fix.** Add `Outbox:MaximumAttempts` (and the saga equivalent); on
exhaustion, move the row to a `outbox_dead_letters` table in the same
transaction that removes it from the pending set, emit
`OrdersTelemetry.RecordOutboxDeadLettered`, and alert on it separately from
backlog growth. Raise DLQ `retention.ms` to 14-30 days and give the topics
`--partitions 3` so a redrive can parallelize. Neither change is large;
both are the difference between "we can reconstruct what happened" and "we
cannot."

### Severity 3: a Polly retry wraps whole database transactions

`apps/src/Orders.Worker/SagaOrchestrationStore.cs:183-210` and
`apps/src/Orders.Worker/OrderStatusStore.cs:109-139` both open a
transaction, do a conditional write, enqueue outbox rows and commit —
**inside** `_pipeline.ExecuteAsync`, which retries twice on transient
`NpgsqlException`, `TimeoutException` or `IOException`
(`ResilienceExtensions.cs:21-32`).

The dangerous case is a commit whose acknowledgement is lost: Postgres
applied it, the client saw an `IOException`, Polly retries the entire
lambda. The CAS guards mean the retry does not corrupt anything — but it
returns the wrong answer. `TryAdvanceSql`'s `WHERE … step = @expected_step`
now matches nothing, so `TryAdvanceAndQueueAsync` returns `null`, the reply
consumer logs `UnknownReply` and drops the reply
(`OrderSagaReplyConsumer.cs:148-152`), and **the saga stalls until the
timeout sweeper resolves it** — which, per the Severity 1 findings above,
is itself the least reliable path in the system.
`ClaimTimedOutAndQueueAsync` has the same shape, where a lost commit
acknowledgement means the sweeper never runs `ResolveAsync` for rows it
already deleted.

**Fix.** Move the retry inside the unit of work rather than around it:
retry connection acquisition and each statement, not the transaction. Where
retrying the whole transaction really is wanted, make it idempotent by
detecting "already applied" — re-reading the row after a failed CAS and
distinguishing "someone else moved it" from "I moved it and lost the ack"
via a writer identity column.

### Severity 3: the anti-entropy sweep can only ever see the newest rows

`apps/src/Orders.Worker/AntiEntropySweeper.cs:280-291` and `243-253`

Both database-driven checks are `ORDER BY created_at DESC LIMIT @batch_size`
with `BatchSize = 200` (`AntiEntropyOptions.cs:23`). `AntiEntropyOptions`
is honest about this ("Bounded, not paginated across the whole table"), but
the operational consequence deserves naming: **once the store exceeds 200
orders per sweep interval, older divergences become permanently
invisible**, and the rows the sweep does examine are the newest ones —
which are also the ones most likely to be legitimately mid-flight, i.e. the
highest false-positive population and the lowest true-positive one. The
sweep is aimed at exactly the wrong end of the table.

Two smaller problems in the same class:

- `CheckOrdersHaveAccountedPaymentsAsync` issues **one HTTP GET per
  candidate order, sequentially** (line 93-98) — up to 200 serial round
  trips to `Payments.Service` per tick, through a client whose failure is
  swallowed per-order (line 110-117). A batch
  `POST /payments/by-orders` returning a map would be one call.
- There is no check for the failure mode that Severity 1 above actually
  produces: an order stuck non-terminal with no saga row.

**Fix.** Replace `ORDER BY … DESC LIMIT` with a durable cursor (a
`anti_entropy_progress` table holding the last-swept `created_at`/`id`,
advanced each tick and wrapping to the start of the table) so the sweep
covers the whole store in bounded time. Batch the payments lookup. Add the
stuck-order check.

### Severity 3: the Kafka consumer commits synchronously, one message at a time

`apps/src/BuildingBlocks.Messaging/KafkaConsumerHost.cs:73-81`

`consumer.Commit(consumeResult)` is a **blocking, synchronous round trip to
the group coordinator, executed once per message**, on the same thread that
must return to `Consume` to keep the assignment healthy. Combined with the
strictly sequential `Consume → process → commit` loop (line 52-82), one
consumer instance's throughput is capped at roughly
`1 / (processing_latency + commit_rtt)` messages per second, and a single
Postgres transaction under a per-SKU advisory lock
(`InventoryReservationMessageProcessor.cs:72-73`) is the processing
latency. Horizontal scale is bounded by partition count, which is 3.

Two related gaps in the same class:

- No `SetPartitionsRevokedHandler` / `SetPartitionsAssignedHandler`. During
  a rebalance, an in-flight message's `Commit` for a revoked partition
  throws; it is caught as a generic `KafkaException` and treated as an
  infrastructure blip (line 77-81), which is survivable only because every
  consumer is inbox-deduplicated. It should be explicit, not incidental.
- `consumer.Seek(consumeResult.TopicPartitionOffset)` on an infrastructure
  fault (line 69) re-reads one message in a tight loop bounded only by
  `InfrastructureRetryDelayMilliseconds` = 1 s, with no ceiling and no
  metric distinguishing "retrying" from "stuck."

**Fix.** Switch to `StoreOffset` plus librdkafka's periodic offset commit
(`EnableAutoCommit = true`, `EnableAutoOffsetStore = false` — the second
half is already set, line 37), which preserves the exact at-least-once
semantics the inbox relies on while removing the per-message round trip.
Add explicit rebalance handlers. Then re-measure: this is the single change
most likely to move end-to-end saga latency, and it should be validated
with the existing k6 harness before and after.

### Severity 4: the BFF proxy loses headers, and buffers every body in memory

`apps/src/Storefront.Service/ProxyEndpoints.cs:74-134`

- `ForwardOrderAsync` (line 98-119) forwards **only** `Authorization`. A
  client's own `Idempotency-Key` is dropped, silently disabling the
  durable idempotency `Orders.Api` reads at
  `OrderEndpoints.cs:30`. `StorefrontEndpoints.CheckoutAsync` does set the
  header on its own path (line 324-327), so the cart-driven flow is safe —
  but the documented passthrough that k6, Pact and the README quickstart
  use is not.
- `X-Correlation-ID` is never forwarded on any route, so the correlation id
  a caller supplies is discarded and `CorrelationIdMiddleware` mints a new
  one (`CorrelationIdMiddleware.cs:14-16`). Traces still stitch via
  `traceparent`; log correlation across the BFF boundary does not.
- `WriteResponseAsync` (line 124-134) copies the status code and
  `Content-Type` and nothing else — so `Retry-After` on a 429 from
  `DistributedRateLimitingMiddleware.cs:44`, `Location`, `ETag` and
  `Idempotency-Replayed` (`OrderEndpoints.cs:106`) are all dropped before
  reaching the browser.
- Both directions are fully buffered — `ReadToEndAsync` on the request
  (line 81-83) and `ReadAsByteArrayAsync` on the response (line 126) —
  with no size limit, so response size is bounded only by whatever the
  upstream returns.

**Fix.** Introduce an explicit forward/hop-by-hop header allowlist
(`Authorization`, `Idempotency-Key`, `X-Correlation-ID`, `Accept`,
`Content-Type` outbound; everything except hop-by-hop inbound), and stream
both directions with `StreamContent` and a configured max body size. This
is the one finding where adopting YARP instead of hand-rolling is worth
considering on its own merits.

### Severity 4: smaller things worth naming

- **Bestsellers double-count on redelivery.** `OrderSagaReplyConsumer` has
  no inbox; its idempotency comes entirely from the saga state machine's
  guards, which is correct for the saga. But
  `RecordSaleBestEffortAsync` (line 354-374) runs *after*
  `TryCompleteAndQueueAsync` has committed, and a redelivered commit reply
  re-runs the handler up to the `completed is null` branch — the counter
  increments again. Cosmetic today (analytics), but it is the same
  claim-then-act shape as Severity 1.
- **Capture and cancel have no inbox.** `PaymentSettlementProcessor`
  dedups refunds against the inbox (line 100-113) but relies solely on
  domain-state guards for capture and cancel. Sound, except for one edge:
  a capture arriving before the payment row exists returns `Processed` and
  commits the offset (line 84-90) — a permanently uncharged shipped order
  if the ordering assumption ever breaks. Prefer an explicit "no payment
  yet, retry" over a silent ACK.
- **Leader election has no fencing token.** `LeaderElectionService.IsLeader`
  is a `volatile bool` (line 66-68); a leader partitioned from the API
  server keeps returning `true` until the k8s client notices. Both sweepers
  hold `FOR UPDATE SKIP LOCKED` during their claims, which is what actually
  makes a double-sweep safe — so the lease is an optimization, not the
  correctness mechanism. That is fine, but the class comments describe it
  the other way round (`SagaTimeoutSweeper.cs:12-14`), which will mislead
  the next person tuning it.
- **One shared Postgres circuit breaker per process.** `PostgresPipeline`
  (`ResilienceExtensions.cs:18-41`) combines a 2-second timeout with a
  breaker at 50% failure over 4 requests, and *every* Postgres caller in
  the process shares that single registry entry. One slow query family
  (e.g. `ClaimTimedOutAndQueueAsync` sweeping 100 rows with per-row outbox
  writes, comfortably over 2 s under load) can trip the breaker for
  everything else, including the readiness check. Split by workload, or at
  minimum give the sweepers their own pipeline with a timeout that matches
  their batch size.
- **The shared outbox publishes 5 rows at a time, one await each.**
  `BatchSize = 5` (`OutboxOptions.cs:7`) with a sequential `foreach`
  (`OutboxPublisher.cs:113-116`) is a low ceiling for a system that fans
  one order out to several events. Raising the batch and producing
  concurrently (bounded, preserving per-key order via the Kafka key) is
  straightforward now that the publish happens outside the transaction.
- **`orders.created.v1` retains 24 hours.** `compose.yaml:417`. The
  event store is Postgres-backed so nothing is *lost*, but "rebuild the
  projection by replaying the topic" — a property the CQRS docs claim — is
  only true for a day.
- **Rate limiting has no global ceiling.** `DistributedRateLimitingMiddleware`
  keys strictly per caller (line 65-68), so N accounts get N budgets and
  the cluster has no aggregate cap. Fail-open on Redis (correct) means the
  cap disappears entirely during a Redis incident. A cheap global
  concurrency limiter behind the per-caller one would bound the worst case.

---

## Part 2 - The plan

Ordered so that each phase makes the next one measurable. Every phase ends
with the same evidence standard the rest of `docs/` holds: a live run under
Compose or K3s, with fault injection where the finding is about faults.

### Phase 1 - Milestone 91: stop the two ways an order can be lost

The Severity 1 findings, in dependency order. Nothing else in this roadmap
is worth doing while an order can silently strand.

1. Rewrite `SagaOutboxPublisher` to claim-publish-mark, reusing
   `OutboxOptions.ClaimWindowSeconds`' existing semantics. Delete the
   long-transaction path entirely rather than shortening it.
2. Fold the order status transition into the same transaction as the saga
   row deletion in `ClaimTimedOutAndQueueAsync`, and into
   `TryCompleteAndQueueAsync` for the reply-consumer paths.
3. Re-derive `SagaOrchestration:TimeoutSeconds` from a measurement, and add
   the `ValidateOnStart` invariant tying it to the consumer retry budget.

**Proof.** Toxiproxy on the Kafka listener with a 30-second latency
toxic while a k6 order load runs: before, expect stranded orders and a
long-running transaction visible in `pg_stat_activity`; after, expect zero
of both and a bounded, growing `saga_outbox_pending`. A `pg_stat_activity`
query for `state = 'idle in transaction'` durations belongs in the
milestone report.

### Phase 2 - Milestone 92: make failure survivable and visible

1. `Outbox:MaximumAttempts` plus an `outbox_dead_letters` table for both
   outboxes, with its own metric and alert.
2. DLQ topics to 14-30 day retention and 3 partitions.
3. Readiness trimmed to what each service genuinely cannot serve without;
   Kafka and Orders-Redis moved to a non-gating `/health/dependencies`.
4. `KafkaHealthCheck` off the thread pool.

**Proof.** Kill Redis and assert `Orders.Api` still serves `POST /orders`
and stays `Ready`. Publish a deliberately un-dispatchable outbox row and
assert it reaches `outbox_dead_letters` and the alert fires, rather than
retrying forever.

### Phase 3 - Milestone 93: causality instead of clocks

1. Per-order monotonic version on `order_events`, carried on
   `OrderStatusChanged`, used as the projection's ordering guard in place
   of `decided_at`.
2. Move the Polly retry inside the unit of work in `SagaOrchestrationStore`
   and `OrderStatusStore`.
3. Split `PostgresPipeline` by workload so a sweeper cannot break the
   request path.

**Proof.** A deterministic simulation test (the existing harness) that
delivers status events reversed and skewed, asserting the projection
converges to the write model. This is also the phase where the TLA+ model
should gain the "reply arrives after the saga row is gone" state, since
that is now a modelled outcome and not just a code comment.

### Phase 4 - Milestone 94: reconciliation that actually covers the store

1. Durable cursor for the anti-entropy sweep, wrapping the whole table.
2. Batch payments lookup endpoint, replacing the 200 serial GETs.
3. The stuck-order check: non-terminal, older than the saga timeout, no
   saga row.

**Proof.** Seed a divergence at the *oldest* end of a table with >
`BatchSize` rows and assert the sweep finds it within a bounded number of
ticks — the property the current implementation cannot satisfy at all.

### Phase 5 - Milestone 95: throughput

Only after Phases 1-4, because every one of them changes the numbers.

1. `StoreOffset` + periodic commit in `KafkaConsumerHost`, with explicit
   rebalance handlers.
2. Raise both outbox batch sizes; bounded-concurrency publish.
3. Re-baseline with k6 and record the before/after in the milestone report,
   including which of the three changes actually mattered.

### Phase 6 - Milestone 96: the BFF boundary

Header allowlist, streaming bodies, response header pass-through, body size
limits. Small, self-contained, and the one place where replacing
hand-rolled code with YARP should be evaluated rather than assumed.

### Deliberately not planned here

- Milestones 97-99 are left open on purpose; the measurements from Phase 5
  should choose them.
- The Severity 4 "smaller things" list is not a phase. Each item is a
  half-day change that belongs in whichever phase touches its file — the
  bestsellers counter with Phase 1, the rate-limit ceiling with Phase 2,
  the topic retention with Phase 5.
- Nothing in Part 1 argues for a new framework, a service mesh change, or a
  different broker. Every finding is a parameter, a transaction boundary,
  or a missing reconciliation in code that already exists.

## Ordering rationale

Phase 1 is first because it is the only phase where the failure is *silent
and permanent*: an order that strands in `Created` produces no alert, no
DLQ entry and no divergence count, and the anti-entropy sweep that would
catch it is added in Phase 4. Phase 2 is second because it converts the
remaining failure modes from silent to loud, which is what makes Phases
3-6 verifiable rather than argued. Throughput is fifth, not first, because
every measurement taken before Phase 3 would have to be retaken after it.
