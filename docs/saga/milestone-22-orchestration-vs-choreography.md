# Milestone 22 Orchestrated Saga vs Choreographed Saga

## Scope

Every prior milestone's saga work (M11-13, M16) exercised the choreographed design: Payments.Service autonomously decides on seeing `OrderCreated`, no service explicitly asks it to and no service is watching for it to fail to answer. This milestone builds an orchestrated version of the exact same saga - purely additive, a second consumer group on the same `orders.created.v1` topic, so the existing choreography is completely untouched - and compares them on the axes that actually differ: failure handling, traceability, and coupling. Not as an abstract comparison; each claim below was actually broken or actually demonstrated live.

## Design

- **`OrderSagaOrchestrator`** (Orders.Worker): a second, independent consumer group (`orders-saga-orchestrator`) on the same `orders.created.v1` topic the choreographed consumers already read - adding a subscriber to an existing topic is inherently risk-free to the existing ones, which is what made this comparison possible without touching anything already validated. On each order, it explicitly publishes a `PaymentDecisionRequested` command (a new topic, `payments.decision-requested.v1`) and tracks the pending request in memory.
- **`PaymentDecisionRequestHandler`** (Payments.Service, superseded by `PaymentDecisionRequestProcessor` in Milestone 65 - see the update at the end of this document): the orchestrated counterpart to the existing choreographed `OrderCreatedConsumer`, applying the identical amount-threshold decision - but deliberately stateless. No database write, no outbox. In choreography, Payments.Service owns persisting the decision because nothing else will; in orchestration, the orchestrator owns the saga's state, so this side doesn't need its own. That's the coupling difference measured below, not just asserted.
- **`SagaTimeoutSweeper`**: polls the in-memory tracker every second and marks any request older than 5 seconds as timed out - the orchestrator's explicit compensation path. In-memory state (not a database table) is a deliberate scope boundary: the comparison is about the orchestrator's explicit *ownership* of timeout and completion, not building a durable saga-persistence layer a real production orchestrator would need.
- **No schema registry, no Avro** for the two new command/reply topics - `PaymentDecisionRequested`/`PaymentDecisionReplied` (plain JSON, `BuildingBlocks`) are internal, transient messages between one orchestrator and one responder, not a domain event other future consumers would ever need to evolve against independently, unlike `OrderCreated` in Milestone 19.

## What didn't work

**`kubectl scale --replicas=0` doesn't work against a resource Argo CD manages - the Milestone 15/19 lesson recurring a third time, in a new shape.** To demonstrate the orchestrator's timeout, `payments-service` needed to go down without answering. `kubectl scale deployment payments-service --replicas=0` appeared to succeed but the pod count stayed at 1 - `selfHeal: true` reverted it, same mechanism as the earlier manifest-drift incidents, just triggered by a scale command instead of an `apply`. This time the fix wasn't "commit first" (there was nothing to commit - this needed to be a *temporary* operational action, not a permanent state change) but the actual sanctioned Argo CD pattern for that situation: `kubectl patch application ... -p '{"spec":{"syncPolicy":{"automated":null}}}'` to pause auto-sync, perform the manual scale, run the experiment, scale back up, then restore `automated: {prune: true, selfHeal: true}`. Confirmed `Synced`/`Healthy` afterward.

**The new consumer groups used `AutoOffsetReset.Latest` where every existing consumer in this codebase uses `Earliest` - an inconsistency that silently dropped the very message the timeout demo depended on.** The first timeout attempt showed no `OrchestratedSagaTimedOut` log at all; investigating, the request-consumer group had no committed offset yet when its pod was scaled down (the 1-second auto-commit interval hadn't fired before the pod died), so on restart it fell back to `Latest` and picked up *after* the message it needed to see. Every choreographed consumer already sets `AutoOffsetReset.Earliest` specifically to guard against exactly this - a copy-paste inconsistency across the three new consumers, not a deliberate choice, fixed by matching the established convention. Rebuilding and rerunning the exact same experiment then produced the expected `OrchestratedSagaTimedOut` log at the 5-second mark.

**The orchestrated flow has no inbox-based deduplication, unlike its choreographed counterpart - an intentional scope boundary, made visible by the very act of redeploying.** After the offset-reset fix, a fresh order's logs showed `OrchestratedSagaRequested`/`OrchestratedSagaCompleted` three times each for the same order - the redeploy's pod cycling replayed a handful of already-processed messages before the new offsets stabilized. Harmless here (both the request and the decision are idempotent - deciding the same order's payment twice yields the same deterministic answer), but a real production orchestrator would need the same `InboxStore`-style deduplication the choreographed consumers already have. Left out deliberately: the milestone is comparing coupling and failure-handling architecture, not re-deriving exactly-once processing a second time.

## Results

### Normal case: both flows converge

| Flow | Result |
| --- | --- |
| Choreographed (existing, unmodified) | Order reaches `Confirmed`/`Cancelled` via `PaymentResultConsumer`, as in every prior milestone |
| Orchestrated (new, parallel) | `OrchestratedSagaRequested` -> `OrchestratedSagaCompleted approved=True latencyMs=175.1`, same order, same instant, zero interference with the choreographed path processing it simultaneously |

### The actual comparison: Payments.Service down, no reply ever comes

| Flow | Observed |
| --- | --- |
| Choreographed | Order status: `Created` - and stays there, indefinitely, with no signal anywhere that anything is wrong. Nothing is watching for the absence of a `PaymentDecided` event; the only thing that "notices" is a human explicitly querying the row. |
| Orchestrated | `OrchestratedSagaTimedOut order ... after 5s` - logged automatically, in real time, without anyone querying anything |

### A genuine nuance, not glossed over: choreography does eventually self-heal

Once `payments-service` was scaled back up, its choreographed `OrderCreatedConsumer` resumed from its uncommitted Kafka offset (the message was never lost - Kafka retained it) and the stuck order transitioned to `Confirmed` on its own, no restart or manual intervention needed beyond bringing the service back. The real difference isn't "choreography breaks forever" - it doesn't. It's *detection*: the orchestrator flagged the problem within 5 seconds of it happening, independent of whether or when Payments.Service ever comes back; choreography's recovery is real, but silent until it happens, and nothing shortens the gap between "broken" and "someone notices" on its own.

### Regression check

`k3s-smoke-test.sh` and `k6-run.sh saga` (`failed_rate=0`, `saga_correct_outcome_rate=99.70%`, consistent with this lab's already-documented baseline) both pass cleanly - the choreographed path is provably unaffected by any of this milestone's additions.

## Running the experiment

```bash
# Normal case
curl -X POST http://<orders-api>/orders -d '{"customerId":"demo","amount":49.90,"currency":"BRL"}'
kubectl logs -n orders-lab -l app.kubernetes.io/name=orders-worker | grep OrchestratedSaga

# Timeout case (pause Argo CD auto-sync first - see "what didn't work" above)
kubectl scale deployment payments-service -n orders-lab --replicas=0
curl -X POST http://<orders-api>/orders -d '{"customerId":"demo","amount":49.90,"currency":"BRL"}'
# wait >5s, then:
kubectl logs -n orders-lab -l app.kubernetes.io/name=orders-worker | grep OrchestratedSagaTimedOut
kubectl scale deployment payments-service -n orders-lab --replicas=1
```

## Milestone 65 update: leveling the reliability gap

### Why this update exists

Milestone 43 grew the orchestrator from a single request/reply pair into a real 4-step saga (Reserve Inventory -> Decide Payment -> Commit or Release Inventory), but `PaymentDecisionRequestHandler` - the orchestration path's payment-decision step - stayed exactly as described above: no inbox dedup, no `Payment` row, no outbox, no DLQ, direct produce-and-hope. Meanwhile the choreographed path (`PaymentMessageProcessor`) had all of that from the start. So every comparison in this document up to this point, including the "orchestrated detects, choreographed doesn't" result, was implicitly comparing a hardened implementation against an unhardened one - a real difference, but not the one this milestone claims to measure. A fair comparison needs both sides carrying the same reliability guarantees.

### What changed

- **`PaymentDecisionRequestProcessor`** (Payments.Service) replaces `PaymentDecisionRequestHandler`. Same inbox-dedup -> decide -> outbox pattern as `PaymentMessageProcessor`, adapted for the request/reply shape: since `PaymentDecisionRequested` carries no `EventId` of its own (Milestone 22's own design - a transient command, not a domain event), the order ID serves as the inbox key, valid because the orchestrator only ever has one decision request outstanding per order. Runs on the shared `KafkaConsumerHost<TValue>`/`OutboxPublisher<TDbContext>`/`KafkaDeadLetterPublisherBase<TValue>` abstractions this cleanup effort's Fase 4 built for the choreographed consumers - this milestone is also the first real test of whether those abstractions generalize to a second, structurally different flow, not just a second copy of the same one.
- **`OrderSagaReplyConsumer`** now writes the order's actual status via `OrderStatusStore` at every terminal saga outcome (`Confirmed` on commit, `Cancelled` on insufficient stock or on the payment-declined compensation path) and invalidates the read cache, matching what the choreographed `PaymentResultProcessor` already did. Before this fix, a deployment running pure `Orchestration` mode would have completed the saga correctly in Kafka and Postgres's saga-tracking table, while every order stayed stuck at `Created` forever from the customer's point of view - the saga machinery worked, but nothing told the `orders` table about it.
- **`SagaMode`** (`Choreography` / `Orchestration` / `Both`) gates which hosted services each instance runs: `Choreography` is the default (today's behavior, unchanged), `Orchestration` runs only the orchestrator/reply-consumer/decision-processor path, `Both` runs everything simultaneously against identical traffic - the intended way to compare them, since both paths race to answer the same order and whichever completes first wins the (idempotent) `Created -> Confirmed`/`Cancelled` transition.

### A second pre-existing bug this validation exposed - not pre-existing to the repo, but to this cleanup session's own Fase 4

Running `Orchestration` mode end-to-end for the first time (nothing had ever exercised it before - `Choreography` never touches the commit/release inventory steps) surfaced a real bug: orders reached the `CommitInventory` step and then stopped, forever, with the `InventoryReservationCommitRequested` message sitting unconsumed in Kafka.

Root cause: `Inventory.Service/Program.cs` registers three consumers - reservation-requested, commit-requested, release-requested - all as `KafkaConsumerHost<string>`, via `builder.Services.AddHostedService(serviceProvider => new KafkaConsumerHost<string>(...))`. `AddHostedService<THostedService>(factory)` calls `TryAddEnumerable` internally, which deduplicates by *closed generic type* - and all three factories close over the identical `KafkaConsumerHost<string>`. Only the first registration survives; the other two are silently dropped, no exception, no log line, nothing - `dotnet` never even attempts to construct them. `Orders.Worker` had the same defect for a different pair: `OrderCreatedConsumer` and `OrderProjectionConsumer` are both `KafkaConsumerHost<byte[]>`, so `OrderProjectionConsumer` (the read-model projector) had been silently dead since Fase 4 introduced the generic `KafkaConsumerHost<T>` - confirmed by `orders-projector` never appearing in the server's Kafka consumer-group list, in any of this cleanup session's earlier "everything's healthy" validations.

This is a bug this session introduced (in the Fase 4 deduplication of per-service consumer classes into a shared generic host), not one that predates it - it just had no way to surface until Fase 5 finally exercised the code paths (orchestrated commit/release, order projections) that Fase 4's own validation never touched. Fixed by switching every `KafkaConsumerHost<T>`-returning factory registration from `AddHostedService(factory)` to `AddSingleton<IHostedService>(factory)`, which registers unconditionally instead of through `TryAddEnumerable`'s dedup - across `Inventory.Service`, `Orders.Worker`, and `Payments.Service` for consistency, even where no actual type collision existed yet.

### Real measurements (Docker Compose, lab server, 2026-08-05)

All three `Saga:Mode` values were exercised against the same live stack (Postgres, Kafka, Keycloak-authenticated `POST /orders`), one order at a time:

| Mode | Order outcome | Saga final-step latency | Notes |
| --- | --- | --- | --- |
| `Choreography` | `Confirmed` | (unchanged, choreography path untouched by this milestone) | Regression check: still converges normally after the `KafkaConsumerHost` fix, which choreography's `OrderCreatedConsumer`/`PaymentResultConsumer` pair never collided on |
| `Orchestration` | `Confirmed` | 8.99 ms (`DecidePayment` -> `CommitInventory` -> completed) | Fresh order, full 4-step saga, hardened decision step |
| `Orchestration` | `Cancelled` | n/a (compensation path) | Amount above `PaymentDecision:DeclineAmountThreshold` -> `PaymentDecisionReplied approved=false` -> `ReleaseInventory` -> `OrderStatusStore.TryCancelAsync` |
| `Both` | `Confirmed` | n/a | Choreographed `PaymentResultProcessor` and orchestrated `OrderSagaReplyConsumer` both processed the same order concurrently; `OrderStatusStore`'s conditional `UPDATE ... WHERE status = 'Created'` made the race idempotent - first writer wins, second is a no-op, no error either way |

Two orders placed *before* the `Inventory.Service` fix (while stuck at `CommitInventory`) completed automatically once the fix was deployed and inventory-service's commit-requested consumer started reading its backlog - `finalStepLatencyMs` for those two read ~611s/~620s, which is the request-to-fix wall-clock gap, not a real processing latency; included here only as evidence that no messages were lost while the consumer was silently absent (Kafka retained everything, exactly as the choreography self-healing story earlier in this document already established for a different failure mode).

### Regression check

Local: full non-Docker-dependent test suite (99 tests across `Orders.UnitTests`, `Storefront.UnitTests`, `Orders.ArchitectureTests`, `Services.ArchitectureTests`, `Orders.ContractTests`) green after every Fase 5 change. Server: `Choreography` mode re-verified end-to-end after the `Inventory.Service`/`Orders.Worker` consumer-registration fix, confirming the fix didn't regress the path that had already been validated in earlier milestones.
