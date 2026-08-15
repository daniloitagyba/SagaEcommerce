# Architecture and Cross-Cutting Review — Implementation Plan (2026-08-15)

Third audit in the current series, and deliberately a different lens from the
first two:

- [`audit-2026-08-14-service-and-business-rule-review.md`](audit-2026-08-14-service-and-business-rule-review.md)
  went through the *business rules* of Orders/Payments/Inventory/Cart/Storefront
  (13 findings, all closed in `84cd877`).
- [`audit-2026-08-15-frontend-catalog-infra-review.md`](audit-2026-08-15-frontend-catalog-infra-review.md)
  covered the React frontend, `Catalog.Service`, and the deployment layer
  (remediation currently in the working tree).

Neither examined the system **as a system**: the shared building blocks, the
cross-service contracts, the operational surface, and the places where a
pattern was introduced correctly in one service and then re-implemented,
half-applied, or left unwired everywhere else. That is what this pass covers.

**Method.** Rather than re-reading the milestone narratives, I traced what the
code actually wires up: which abstraction got extracted and which callers still
carry their own copy, which metric has a call site, which endpoint has a client,
which alert exists for which failure mode, and which configuration value has to
be written correctly in more than one file to work. Every finding below points
at the file that proves it.

**Nothing here is a live outage.** The two P0s from the previous pass were the
last of those. What follows is structural: places where the architecture's own
guarantees are weaker than the design intends, and where the next change is
likely to reintroduce a bug class this repo has already paid for.

---

## Executive summary

| # | Finding | Severity | Theme |
|---|---|---|---|
| 1 | Argo CD `selfHeal` fights the HPA and KEDA over `replicas` | P0 | Deploy |
| 2 | `AddStandardResilienceHandler()` is effectively disabled at all 9 call sites | P1 | Resilience |
| 3 | The Kafka broker address is configured in 17 separate places | P1 | Config |
| 4 | Failure-mode metrics exist; alerts for them do not | P1 | Observability |
| 5 | No CI gate on Compose ↔ Kubernetes configuration parity | P1 | Process |
| 6 | The inbox pattern is hand-rolled 8 times next to a shared `OutboxPublisher` | P1 | Messaging |
| 7 | Mesh authorization covers one of seven workloads | P1 | Security |
| 8 | Readiness gates on dependencies the code already degrades around | P2 | Resilience |
| 9 | Rate limiting protects the internal API, not the public origin | P2 | Resilience |
| 10 | Supply-chain controls do not cover the path that actually deploys | P2 | Security |
| 11 | `orders-worker` is eight independent workloads in one lifecycle | P2 | Structure |
| 12 | Idempotency observability was lost in the Redis → Postgres move | P2 | Observability |
| 13 | Three incompatible image-tagging schemes; Compose tags frozen at M42 | P2 | Deploy |
| 14 | `orders-worker` borrows `orders-api`'s client credentials | P2 | Security |
| 15 | 10 dead-letter publisher classes wrapping one base class | P2 | Messaging |
| 16 | The gRPC `OrderQuery` service has no client anywhere | P2 | Dead code |
| 17 | 24h Kafka retention under a "durable event log" and a replay drill | P2 | Messaging |
| 18 | `Saga:Mode` must match across two services; nothing enforces it | P3 | Config |
| 19 | Correlation id and response headers stop at the BFF | P3 | Observability |
| 20 | Consumer retry budget can outlive `max.poll.interval.ms` | P3 | Messaging |
| 21 | Smaller loose ends (choreography, placeholder file, two CSS systems) | P3 | Cleanup |

---

## 1. Argo CD `selfHeal` fights the HPA and KEDA over `replicas` (P0)

`kubernetes/argocd/application.yaml:18-20` enables `automated.selfHeal: true`
with **no `ignoreDifferences` block at all**. Meanwhile:

- `kubernetes/base/orders-api.yaml:9` hardcodes `replicas: 3` on the Rollout,
  and `kubernetes/base/orders-api-hpa.yaml` scales that same Rollout.
- `kubernetes/base/orders-worker.yaml:9` hardcodes `replicas: 1`, and
  `kubernetes/base/orders-worker-scaledobject.yaml` gives KEDA
  `minReplicaCount: 1` / `maxReplicaCount: 3` over that same Deployment.

This is the canonical GitOps/autoscaler conflict. The moment the HPA or KEDA
moves `spec.replicas` away from the manifest value, Argo CD sees drift and
self-heals it back. Depending on which controller writes last, autoscaling
either flaps or is silently pinned — and the scale-up path is exactly what the
KEDA lag triggers and the `orders-api-analysis-template.yaml` canary exist to
exercise. The progressive-delivery and autoscaling milestones both rest on a
behaviour this one missing field can nullify.

**Fix.** Add to the Application:

```yaml
  ignoreDifferences:
    - group: argoproj.io
      kind: Rollout
      name: orders-api
      jsonPointers: ["/spec/replicas"]
    - group: apps
      kind: Deployment
      name: orders-worker
      jsonPointers: ["/spec/replicas"]
```

and drop the hardcoded `replicas` from both manifests so git no longer states a
value it does not own. Verify with a load run: `kubectl get rollout,deploy -w`
should show the replica count *stay* where the autoscaler put it across at least
one Argo CD sync cycle.

---

## 2. `AddStandardResilienceHandler()` is effectively disabled at all 9 call sites (P1)

Every outbound HTTP client in the system follows the same shape:

```csharp
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((sp, client) =>
{
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(3);   // or 5
}).AddStandardResilienceHandler();
```

Nine call sites, all with defaults:
`Orders.Api/Program.cs:106`, `Orders.Worker/Program.cs:130,159,165`,
`Cart.Service/Program.cs:49`, `Storefront.Service/Program.cs:54,60,66,72`.

`Microsoft.Extensions.Http.Resilience` 10.0.0 (`apps/Directory.Packages.props:25`)
defaults to a retry strategy with a **2-second base delay**, exponential backoff
and jitter, and a circuit breaker with `MinimumThroughput = 100` over a 30-second
sampling window. `HttpClient.Timeout` wraps the *entire* handler pipeline,
retries included. So:

- With a 3s outer budget and a ~2s first retry delay, the pipeline cannot
  complete a second attempt. Retries are configured and never happen.
- The failure the caller sees is `TaskCanceledException` from the outer timeout,
  not the upstream's real error — which is why upstream failures surface as
  generic timeouts in this system.
- 100 requests in 30 seconds is well above lab traffic on any of these clients,
  so the circuit breaker never trips either.

The one client whose pipeline is genuinely tuned — Cart's — proves the team
already knows this matters: `CartStore`'s own doc comment explains at length why
it does *not* use the shared 150ms Redis pipeline. The HTTP side never got the
same treatment.

**Fix.** Replace the bare call with an explicit pipeline, per criticality tier,
and keep `HttpClient.Timeout` strictly above the pipeline's total:

```csharp
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout    = TimeSpan.FromSeconds(2);
    options.Retry.MaxRetryAttempts    = 2;
    options.Retry.Delay               = TimeSpan.FromMilliseconds(200);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);
    options.CircuitBreaker.MinimumThroughput = 10;
    options.CircuitBreaker.SamplingDuration  = TimeSpan.FromSeconds(30);
});
```

with `client.Timeout` removed (the pipeline's `TotalRequestTimeout` owns it).
Two tiers are enough: **critical path** (Orders.Api → Catalog, Storefront →
Orders) and **best effort** (Orders.Worker → Catalog for bestsellers,
anti-entropy reads). Put both in `BuildingBlocks.Resilience` as
`AddCriticalHttpResilience()` / `AddBestEffortHttpResilience()` so the next
client cannot silently get the broken shape.

**Proof it works:** `scripts/chaos/resilience-chaos.sh` and the Toxiproxy path
already in `Storefront.Service/Program.cs:72-82` can show a retry actually
completing, which today it cannot.

---

## 3. The Kafka broker address is configured in 17 separate places (P1)

`compose/compose.yaml` sets `BootstrapServers` **16 times** across 11 distinct
option sections:

```
Kafka__BootstrapServers                     (7 services)
PaymentResultKafka__BootstrapServers        compose.yaml:828
OrderProjectionKafka__BootstrapServers      compose.yaml:829
SagaOrchestration__BootstrapServers         compose.yaml:830
OrderEventStore__BootstrapServers           compose.yaml:831
PaymentDecisionRequest__BootstrapServers    compose.yaml:898
PaymentSettlement__BootstrapServers         compose.yaml:902
```

plus a 17th, written by hand in a completely different syntax, in
`kubernetes/base/orders-worker-scaledobject.yaml` (four `bootstrapServers`
entries for KEDA).

Each of these exists because every consumer got its own options class carrying
a full broker configuration — `SagaOrchestrationOptions`,
`PaymentResultKafkaOptions`, `OrderProjectionOptions`, `OrderEventStoreOptions`,
`InventoryKafkaOptions`, `PaymentsKafkaOptions`, `PaymentDecisionRequestOptions`,
`PaymentSettlementOptions` — when the only thing that genuinely varies per
consumer is *topics and consumer group*.

**This is the root cause of a bug class this repo has already paid for five
times**: `Redis__ConnectionString`, four separate Kafka `BootstrapServers`
sections, and `Authentication__Authority` twice (`docs/saga/milestone-75-*`, and
finding #1 of the 2026-08-15 audit). Every recurrence has been fixed at the
symptom. The structure that generates them has not been touched.

**Fix.** Introduce a single broker section and fill down:

1. Define `KafkaConnectionOptions { BootstrapServers, SchemaRegistryUrl }` bound
   once from `Kafka:`.
2. Give every per-consumer options class a *nullable* `BootstrapServers`, and
   register a shared `PostConfigure<T>` that fills it from
   `KafkaConnectionOptions` when unset.
3. Keep the existing `.ValidateOnStart()` assertions — they now fail on a single
   missing root value instead of passing while four sections silently point
   nowhere.
4. Delete the 9 redundant `*__BootstrapServers` lines from `compose.yaml` and
   the equivalents from `kubernetes/base/`.
5. Templatize the KEDA `bootstrapServers` from the same ConfigMap the workloads
   read, so the 17th copy stops being hand-maintained.

Net effect: 17 places become 2 (one Compose, one Kubernetes), and the parity
check in finding #5 has one thing to check instead of eleven.

---

## 4. Failure-mode metrics exist; alerts for them do not (P1)

`BuildingBlocks.Observability/OrdersTelemetry.cs` defines 22 instruments,
including several that exist for no purpose other than to detect the exact
correctness failures this architecture invented mechanisms to prevent:

| Instrument | What a nonzero value means |
|---|---|
| `anti_entropy.divergences` | Two services disagree about a payment or a reservation |
| `saga.orphaned_reply` | A saga reply arrived for state that no longer exists |
| `payments.settlement_reconciliation.unresolved` | A capture/refund could not be reconciled |
| `orders.projection.lag_ms` | The read model is behind the write model |
| `orders.redis.fenced_write_rejected` | A lock holder wrote after its lease expired |
| `orders.cache.bypassed` | Redis is down; reads are hitting Postgres directly |
| `orders.rate_limit.distributed_bypassed` | Redis is down; **rate limiting is off** |

`observability/prometheus/rules/` contains exactly six alerts:
`DeadLetterMessagesDetected`, `KafkaConsumerGroupLagHigh`, `OutboxBacklogGrowing`,
and three `OrdersApiErrorBudgetBurn*`. **Not one of the seven above is alerted
on.** The system measures its own divergence carefully and pages nobody when it
diverges.

The last three are worse than unmonitored, because they are *fail-open*
degradations. `RedisSlidingWindowRateLimiter.cs:70-72` catches infrastructure
faults and allows the request; `RedisOrderCache.cs:36-39` does the same for
reads. Both are correct designs — availability over enforcement — but they turn
a Redis outage into "rate limiting is silently disabled" with no signal.

**Fix.** Add `observability/prometheus/rules/correctness-invariants.yml`:

| Alert | Expression sketch | Severity |
|---|---|---|
| `AntiEntropyDivergenceDetected` | `increase(anti_entropy_divergences_total[15m]) > 0` | page |
| `SettlementReconciliationUnresolved` | `increase(payments_settlement_reconciliation_unresolved_total[15m]) > 0` | page |
| `OrphanedSagaReplies` | `increase(saga_orphaned_reply_total[10m]) > 3` | ticket |
| `RateLimitingFailedOpen` | `increase(orders_rate_limit_distributed_bypassed_total[5m]) > 0` | page |
| `OrderCacheFailingOpen` | `rate(orders_cache_bypassed_total[5m]) > 0` | ticket |
| `FencedWriteRejectedSustained` | `rate(orders_redis_fenced_write_rejected_total[10m]) > 0` | ticket |
| `ProjectionLagHigh` | `histogram_quantile(0.95, orders_projection_lag_ms) > 5000` | ticket |

Wire them into `observability/alertmanager/`. `scripts/live-proofs/projection-lag-test.sh`
and `scripts/chaos/resilience-chaos.sh` already produce the conditions, so each
alert can be *proven to fire* rather than merely written — the standard this
repo holds itself to everywhere else.

---

## 5. No CI gate on Compose ↔ Kubernetes configuration parity (P1)

Five recurrences of the same bug class (see finding #3) and the gate still does
not exist. `.github/workflows/ci.yml` runs
`scripts/infra/verify-compose-network-names.sh` — a check for a *different*,
narrower drift — and nothing else about configuration.

**Fix.** `scripts/ci/verify-config-parity.sh`, added to the `test` job:

1. Parse `compose/compose.yaml` for each app service's `environment:` keys.
2. Parse the paired `kubernetes/base/*.yaml` workload's `env:` names.
3. Fail on any key present in Compose and absent in Kubernetes, with an explicit
   allowlist (in the script, with a reason per entry) for the ones that are
   legitimately Compose-only — `InstanceId`, `ASPNETCORE_ENVIRONMENT`,
   `LeaderElection__Mode`, the `PUBLIC_*` browser-facing values.
4. Extend it to the migration/seed Jobs, which is where the last P0 actually
   bit.

This is roughly 60 lines of `yq` and it retires the bug class permanently.
Do it in the same PR as finding #3 so the check has a small, stable surface
to guard.

---

## 6. The inbox pattern is hand-rolled 8 times next to a shared `OutboxPublisher` (P1)

The outbox half was extracted properly: `BuildingBlocks.Persistence/OutboxPublisher.cs`
is a generic `OutboxPublisher<TDbContext>` with a well-documented claim/publish/
retry contract, an `IOutboxDbContext` seam, and one `IOutboxEventDispatcher`
implementation per service. That is the right shape.

The inbox half was not. `Orders.Worker/InboxStore.cs` is a clean, pipeline-wrapped,
parameterized implementation used by four Orders.Worker processors. Every other
service inlines the same `INSERT ... ON CONFLICT (consumer_name, event_id) DO NOTHING`
as raw SQL:

```
Inventory.Service/InventoryReservationMessageProcessor.cs:78, :329
Inventory.Service/InventoryReservationMessageProcessor.Backorders.cs:121
Inventory.Service/ReplenishmentRequestProcessor.cs:68
Payments.Service/PaymentMessageProcessor.cs:76
Payments.Service/PaymentDecisionRequestProcessor.cs:71
Payments.Service/PaymentSettlementProcessor.cs:107
```

Eight copies of the deduplication rule that makes at-least-once delivery safe.
The Orders.Worker copy runs through `ResilienceExtensions.PostgresPipeline`
(retry + breaker + timeout); the other eight do not. So a transient Postgres
fault during the inbox write behaves differently depending on which service is
consuming, and adding a ninth consumer means transcribing the SQL a ninth time.

**Fix.** Move `InboxStore` to `BuildingBlocks.Persistence` as `InboxStore` /
`IInboxStore`, register it in each service's `Program.cs`, and replace the eight
inline statements with `TryRecordAsync(...)` calls. Note that Inventory and
Payments write their inbox row *inside* the same EF transaction as the business
change — the shared store must therefore accept an optional ambient
`NpgsqlTransaction`/`DbContext` rather than always using its own connection.
That is a real design constraint, not a reason to skip the extraction: the
Orders.Worker consumers write theirs outside a transaction and dedupe on the
Kafka offset instead, and both patterns should be visible in one file with the
difference documented.

`apps/tests/Services.ArchitectureTests` is the natural home for a fitness
function asserting no `INSERT INTO inbox_messages` string literal exists outside
`BuildingBlocks.Persistence`.

---

## 7. Mesh authorization covers one of seven workloads (P1)

`kubernetes/cluster-policies/orders-api-authz.yaml` is the *only* Linkerd
`Server`/`AuthorizationPolicy` pair in the repo. Its own header states the
cluster default is `all-unauthenticated` (Milestone 24). So:

- `cart-service`, `catalog-service`, `inventory-service`, `payments-service`,
  `storefront-service` and `orders-worker` accept inbound traffic from **any**
  pod, meshed or not, with no identity requirement.
- `orders-api`'s gRPC port 8081 has no policy either — documented at the bottom
  of that file as a Linkerd `proxyProtocol` problem that was left open.

The NetworkPolicy that would otherwise backstop this does not.
`kubernetes/base/network-policies.yaml` declares `default-deny-ingress`, then
immediately reopens it with `allow-health-and-api`, which has **no `from:`
selector** — it permits ports 8080/8081/4143/4191 from every source in the
cluster. And `policyTypes` is `Ingress` only across the whole file, so there are
no egress restrictions at all: a compromised pod can reach any service, any
database, and the internet.

The JWT layer is real and does enforce per-caller authorization. But "zero-trust"
as claimed in the README currently means "one workload has mesh identity
enforcement, and the network layer allows everything."

**Fix**, in order of value per unit of work:

1. Add `Server` + `AuthorizationPolicy` for `cart-service`, `catalog-service`,
   `inventory-service` and `payments-service`, each allowing only the identities
   that actually call it (Storefront → Cart/Catalog; Orders.Api → Catalog;
   Orders.Worker → Payments/Inventory). The call graph is small and already
   documented in each `Program.cs`'s HttpClient registrations.
2. Give `allow-health-and-api` a real `from:` — `namespaceSelector` for the
   `linkerd-viz` scrape and `podSelector` for in-namespace traffic.
3. Add `policyTypes: [Ingress, Egress]` with egress allowed to the
   `infrastructure-services` endpoints, kube-dns, and nothing else.
4. Move `cluster-policies/` into the Argo CD-managed overlay, or add a CI job
   that `kubectl diff`s them. Applied imperatively (as the file's own comment
   says), they drift silently and nothing notices.

---

## 8. Readiness gates on dependencies the code already degrades around (P2)

`Orders.Api/Program.cs:126-129` and `Orders.Worker/Program.cs:315-318` both
register Postgres, Kafka **and Redis** under the `ready` tag, so
`/health/ready` fails if any one is unavailable.

But every Redis path in Orders.Api already degrades gracefully by design:
`RedisOrderCache.cs:36-39` falls back to Postgres, and
`RedisSlidingWindowRateLimiter.cs:70-72` fails open. A Redis blip therefore
takes all three `orders-api` replicas out of the Service endpoints — a full
outage — for a dependency the code was explicitly written to survive. This is
the readiness-probe-causes-the-outage pattern.

At the other extreme, `Storefront.Service/Program.cs:90-91` sets **both**
`/health/live` and `/health/ready` to `Predicate = _ => false`, so the public
origin reports ready unconditionally while depending on four upstreams.

Three services, three different readiness philosophies.

**Fix.** Adopt one rule — *readiness means "this instance can serve its
contract"* — and tag accordingly:

- Orders.Api: Postgres `ready` (cannot serve without it); Kafka and Redis moved
  to a new `dependencies` tag exposed at `/health/dependencies`, scraped and
  alerted but not probed.
- Orders.Worker: Kafka and Postgres `ready` (it genuinely cannot consume
  without either); Redis to `dependencies`.
- Storefront.Service: add a real readiness check that its four upstream
  `HttpClient` base addresses resolve, or state in the manifest why "always
  ready" is correct for a stateless proxy — but not by accident.

Codify the choice in each `Program.cs` with a one-line comment, the way the rest
of this codebase documents its trade-offs.

---

## 9. Rate limiting protects the internal API, not the public origin (P2)

`grep -rl RateLimiting apps/src` returns files in `Orders.Api` and
`Orders.Infrastructure` only. There is no rate limiting in
`Storefront.Service` — the single origin the browser talks to and the only
service actually exposed to the network — nor in `Cart.Service`, which takes the
highest-frequency authenticated write in the whole system ("add to cart").

The distributed limiter itself (`RedisSlidingWindowRateLimiter`, Milestone 38) is
good work: Lua-scripted sliding window, keyed by caller identity, fail-open with
a metric. It is simply mounted one hop too far in.

**Fix.** Move `DistributedRateLimitingMiddleware` and its options into
`BuildingBlocks` (it depends only on `IConnectionMultiplexer`, the resilience
pipeline, and `HttpContext.User`), then mount it in `Storefront.Service` keyed
by the shopper's `sub` claim — `UnverifiedJwt.TryGetClaim` already extracts
exactly that for the idempotency key — and in `Cart.Service` keyed by the
authenticated caller. Keep the Orders.Api limiter: defence in depth against
service-to-service callers is still worth having.

---

## 10. Supply-chain controls do not cover the path that actually deploys (P2)

Two independent gaps:

**a) CI publishes before it scans.** In `.github/workflows/ci.yml`, the
`build and push` step runs with `push: true` and tags
`${IMAGE}:${{ github.sha }}` *and* `${IMAGE}:latest` — then the SBOM, the Trivy
scan (`exit-code: "1"` on CRITICAL/HIGH) and the cosign signature run
afterwards. A vulnerable image is already in GHCR, already tagged `latest`, when
the gate fails. The gate blocks the *job*, not the artifact.

*Fix:* build with `load: true` (no push), scan and sign the local image, then
push in a final step gated on the scan. Or push only the immutable
`:${{ github.sha }}` tag first and move `:latest` only after the scan passes.

**b) The Kyverno policy does not evaluate anything that runs.**
`kubernetes/cluster-policies/verify-image-signatures.yaml` says so itself, at
length: `imageReferences` matches `ghcr.io/daniloitagyba/saga-ecommerce/*`,
while `kubernetes/overlays/local/kustomization.yaml` deploys
`saga-ecommerce/*:local` with `imagePullPolicy: Never`. The `require-image-digest`
rule then explicitly excludes `*:local` too. The policy is honest about this,
and the reasoning (don't sign every local rebuild) is sound — but the net result
is that signature verification is never exercised, so nothing would catch it
breaking.

*Fix:* add a CI job running `kyverno apply` against the policy with two fixture
pods — one signed GHCR reference (expect allow) and one unsigned (expect deny).
That validates the policy without slowing the local loop, which is the actual
goal.

---

## 11. `orders-worker` is eight independent workloads in one lifecycle (P2)

`Orders.Worker/Program.cs` registers, in a single Deployment:

- 6 Kafka consumer groups: order-created, payment-result (choreography),
  order-projection, saga request, saga reply, event-store projector
- 4 leader-elected background sweepers: `SagaTimeoutSweeper`,
  `AntiEntropySweeper`, `RetentionSweeper`, `SagaOutboxPublisher`
- Leader election, the Redis bestsellers store, and the cache invalidator

These have genuinely different scaling curves and blast radii. The projection
consumer is throughput-bound and horizontally scalable; the saga orchestrator is
latency-critical; the sweepers are singletons by construction. Today they scale
and fail together.

The autoscaling makes the mismatch concrete.
`kubernetes/base/orders-worker-scaledobject.yaml` triggers on lag in
`orders-worker` and `orders-projector` — but **not** on the saga request/reply
consumer groups or the event-store group. So the critical path (saga replies)
never triggers a scale-up, while projection lag scales up all six consumers plus
four sweepers that leader election will immediately idle on the new replicas.

**Fix.** Split along the seam the code already has — each consumer is registered
as an independent `KafkaConsumerHost` with its own options class, so this is a
deployment change, not a rewrite:

- `orders-saga-worker` — saga request/reply consumers, timeout sweeper, saga
  outbox publisher. Scaled on saga reply lag.
- `orders-projection-worker` — projection + event-store consumers. Scaled on
  their own lag; this is the one that genuinely benefits from replicas.
- `orders-maintenance-worker` — anti-entropy, retention, cache invalidation,
  bestsellers. `replicas: 1`, leader election becomes optional.

Gate each on a `Workload:Role` configuration value read the same way `Saga:Mode`
already is, so one image still serves all three. If that is more restructuring
than is wanted right now, the minimum viable fix is adding the saga consumer
groups to the KEDA triggers — that closes the scaling blind spot on its own.

---

## 12. Idempotency observability was lost in the Redis → Postgres move (P2)

`OrdersTelemetry.RecordIdempotentReplay()` (`:143`) and `RecordIdempotencyBypass()`
(`:148`) have **zero call sites** — in `apps/src`
and in `apps/tests`. They are the only two instruments in the file with none.

They were live when idempotency lived in Redis. The move to a transactional
Postgres record (`Orders.Infrastructure/Data/OrderIdempotencyRecord.cs`,
`EfOrderRepository.cs:61` — `ON CONFLICT (customer_id, idempotency_key) DO NOTHING`)
was the right call and closed a real correctness gap, but it dropped the
instrumentation on the floor.

Consequence: there is no way to answer "how many checkouts are idempotent
replays?" — the single most useful signal for whether clients are retrying,
whether the BFF's `Idempotency-Key` derivation is working, and whether a
duplicate-order incident is happening. `docs/architecture/idempotency-key.md`
describes a mechanism that is now unobservable.

**Fix.** Call `RecordIdempotentReplay()` on the `DO NOTHING` branch in
`EfOrderRepository`, delete `RecordIdempotencyBypass()` (fail-open no longer
exists on this path — say so in a comment), and add the replay rate to the
orders Grafana dashboard.

---

## 13. Three incompatible image-tagging schemes; Compose tags frozen at M42 (P2)

| Where | Tag |
|---|---|
| `compose/compose.yaml` | `:milestone-7`, `:milestone-41-inventory`, `:milestone-42-by-sku`, `:milestone-42-cart-redis-timeout-fix`, `:milestone-46-storefront-web` |
| `kubernetes/base/` | `:latest` |
| `kubernetes/overlays/local/` | `:local` |
| `.github/workflows/ci.yml` | `:${{ github.sha }}` and `:latest` |

The Compose tags are the problem. They are frozen at whatever milestone last
touched each service, and Compose only rebuilds when asked. The README's own
quickstart is:

```bash
docker compose --profile compose-apps up --detach --wait
```

with no `--build`. A user who ran the stack once and then pulls new code gets
the **old images**, silently, because the tag did not change. `cart-service` is
pinned at `milestone-42-cart-redis-timeout-fix` — a debugging tag — while the
CRDT cart work (Milestone 86) has landed since.

**Fix.** Use one scheme: `saga-ecommerce/<service>:dev` in Compose,
`:local` in the K3s overlay (unchanged), `:${sha}` in CI (unchanged), and add
`--build` to the README quickstart. `scripts/infra/k3s-build-images.sh` already
does the right thing for the cluster; Compose just needs to stop pretending its
tags are versions.

---

## 14. `orders-worker` borrows `orders-api`'s client credentials (P2)

`Orders.Worker/Program.cs:133-137` is explicit about it:

```
// This service's own credentials - reuses orders-api-clients (already
// provisioned as this lab's trusted-backend-tooling client) rather than
// provisioning a new one, since it already carries every role and audience
// the sweep below needs.
```

So `orders-worker` presents `orders-api`'s identity to Payments and Inventory
for its anti-entropy reads. Three consequences: the two workloads cannot be
told apart in Keycloak's audit log or in any downstream authorization decision;
`orders-worker` holds every role `orders-api` has, not the two read scopes it
needs; and rotating that one secret takes down both services.

It is also inconsistent with the mesh layer, which *does* give `orders-worker`
its own identity — `orders-worker.orders-lab.serviceaccount.identity.linkerd.cluster.local`
in `cluster-policies/orders-api-authz.yaml:28`.

**Fix.** Add an `orders-worker-client` to `scripts/infra/keycloak-configure-realm.sh`
with only `payments:read` and `inventory:read`, seal its secret alongside the
existing ones in `orders-runtime-sealed-secret.yaml`, and point
`KeycloakOptions` at it. Roughly a 20-line change across the realm script,
the sealed secret, and two env blocks — and it makes the least-privilege story
true rather than aspirational.

---

## 15. 10 dead-letter publisher classes wrapping one base class (P2)

`BuildingBlocks.Messaging/KafkaDeadLetterPublisherBase<TValue>` holds all the
real logic. Above it sit **10 classes and 10 interfaces**, one per consumer:

```
Orders.Worker/{Kafka,PaymentResult,OrderProjection,SagaOrchestration,SagaReply,OrderEventStore}DeadLetterPublisher.cs
Payments.Service/{Payment,PaymentSettlement,PaymentDecision}DeadLetterPublisher.cs
Inventory.Service/InventoryDeadLetterPublisher.cs
```

Each is ~26 lines that declare an interface, subclass the base with a topic and
an encoder, and forward one method. Three of them are even named
`IDeadLetterPublisher` in three different namespaces.

The interfaces buy nothing, because **`KafkaConsumerHost` takes a delegate, not
an interface** (`BuildingBlocks.Messaging/KafkaConsumerHost.cs:24` —
`Func<ConsumeResult<string,TValue>, Exception, int, CancellationToken, Task>`).
The types exist only to give the DI container something distinct to resolve.

**Fix.** Make the base class concrete and constructible —
`new KafkaDeadLetterPublisher<byte[]>(producer, topic, activityName, Convert.ToBase64String)`
— and build it inline at each `KafkaConsumerHost` registration, where the topic
already is. That deletes ~260 lines and 10 DI registrations per the current
`Program.cs` files, with no behaviour change. Straightforward, low-risk, and a
good first PR for whoever picks this up.

---

## 16. The gRPC `OrderQuery` service has no client anywhere (P2)

`apps/src/Orders.Api/Grpc/OrderQueryGrpcService.cs` and
`apps/src/Orders.Api/Protos/order_query.proto` are the *only* files in
`apps/src` and `apps/tests` that mention it. No `GrpcChannel`, no generated
client, no integration test, no k6 profile, no consumer in any other service.

It nonetheless costs: a second Kestrel listener and the HTTP/1-vs-HTTP/2 port
split that `Orders.Api/Program.cs:22-40` explains at length; port 8081 opened
cluster-wide in `network-policies.yaml`; and a documented, unresolved Linkerd
authorization gap (`cluster-policies/orders-api-authz.yaml`, closing comment)
that exists specifically because of this port.

**Fix.** Either give it a real caller — `Orders.Worker`'s anti-entropy sweep
currently makes exactly this kind of internal read over REST and would be the
honest consumer — or remove the service, the proto, the second listener and the
port-8081 NetworkPolicy rule, and note in `docs/cicd/milestone-30-*` that the
gRPC comparison was completed and retired. Keeping an unreachable endpoint with
a known authorization gap is the one option that costs without paying.

---

## 17. 24h Kafka retention under a "durable event log" and a replay drill (P2)

Every topic in `compose/compose.yaml:383-411` is created with
`--config retention.ms=86400000` — 24 hours. Meanwhile:

- The README advertises "a durable event log".
- `Orders.Worker/OrderEventStoreProjector` builds the append-only event store by
  consuming `orders.created.v1`, `payments.result.v1` and
  `orders.status-changed.v1`.
- `scripts/live-proofs/order-projection-replay-drill.sh` exists to rebuild the
  read model from those topics.

So the replay drill can only ever replay the last 24 hours, and rebuilding the
projection from scratch after a longer outage is impossible. The Postgres event
store is the real durable log; Kafka is a 24-hour transport in front of it. That
is a perfectly reasonable design — it is just not what the docs say, and the
drill silently inherits the limit.

**Fix.** Either set `cleanup.policy=compact` (keyed by order id) on the three
event-carrying topics and raise retention on the rest to something matching the
recovery objective, or state the 24-hour replay window explicitly in the drill
script and in `docs/cqrs/`. Prefer the first for `orders.created.v1` — it is
keyed by order id already, so compaction is close to free.

---

## 18. `Saga:Mode` must match across two services; nothing enforces it (P3)

`kubernetes/base/orders-worker.yaml:139` and
`kubernetes/base/payments-service.yaml:71` both carry the comment "Must match
&lt;the other service&gt;'s `Saga__Mode`", as does `compose.yaml:904`. Nothing
checks it. Set `orders-worker` to `Orchestration` and `payments-service` to
`Choreography` and Payments decides autonomously on `OrderCreated` *and* the
orchestrator requests a decision — two payment decisions per order, with no
error anywhere.

**Fix.** Cheapest: have both services log the mode at startup with an identical
message, and add a `k3s-smoke-test.sh` assertion that the two log lines agree.
Better: make it a single ConfigMap key both Deployments reference
(`valueFrom.configMapKeyRef`) and a single `.env` variable in Compose, so there
is only one value to set.

---

## 19. Correlation id and response headers stop at the BFF (P3)

`Orders.Api/Middleware/CorrelationIdMiddleware.cs` is the only implementation in
`apps/src`. Cart, Catalog, Inventory, Payments and Storefront neither accept nor
emit `correlation-id`, even though `MessagingHeaders.CorrelationId` propagates
it faithfully through every Kafka hop.

`Storefront.Service/ProxyEndpoints.cs` compounds it. `ForwardAsync` copies
exactly one header up (`Authorization`), and `WriteResponseAsync` copies exactly
one header down (`Content-Type`). So:

- The correlation id the browser sends is dropped on the way in.
- `Retry-After` on a 429 never reaches the browser — the rate limiter's own
  back-pressure signal is discarded by the only client that could honour it.
- `WWW-Authenticate` on a 401, `Location`, `ETag` and `Cache-Control` are all
  dropped too.

**Fix.** Move `CorrelationIdMiddleware` into `BuildingBlocks.Observability` and
mount it in all six HTTP services; add a small forward/copy allowlist to
`ProxyEndpoints` covering `correlation-id`, `traceparent`, `Idempotency-Key`
upward and `Retry-After`, `WWW-Authenticate`, `Location`, `ETag`,
`Cache-Control` downward.

---

## 20. Consumer retry budget can outlive `max.poll.interval.ms` (P3)

`KafkaConsumerHost.ProcessWithRetriesAsync` retries inline with
`await Task.Delay(...)` while holding the partition assignment
(`BuildingBlocks.Messaging/KafkaConsumerHost.cs:98-127`). `MessageProcessingOptions`
validation caps `MaximumAttempts` at 10 and requires
`MaximumRetryDelayMilliseconds >= InitialRetryDelayMilliseconds` — but places no
ceiling on the maximum delay and never compares the *cumulative* budget against
librdkafka's `max.poll.interval.ms` (default 300 s, not set in `ConsumerConfig`
at `:29-40` — default 300 s applies).

At today's defaults (3 attempts, 5 s cap) this is far from the limit. At the
validated maximum (10 attempts, unbounded cap) a single poisoned message can
hold the assignment past the interval, triggering a rebalance — after which the
subsequent `consumer.Commit()` fails and the message is redelivered to whichever
consumer picked up the partition. Milestone 51 already documented rebalance
versus the per-SKU serialization guarantee; this is another way to provoke one.

**Fix.** Set `MaxPollIntervalMs` explicitly in `ConsumerConfig`, and add a
validation asserting
`MaximumAttempts × MaximumRetryDelayMilliseconds < MaxPollIntervalMs × 0.8`.
Five lines, and it turns an implicit assumption into a startup failure.

---

## 21. Smaller loose ends (P3)

- **Choreography is code no deployment runs.** `Saga__Mode: Orchestration` in
  Compose and in both Kubernetes manifests; the code default
  (`Orders.Worker/Program.cs:213`) is `Orchestration` too. `PaymentResultProcessor`
  and its consumer host are therefore never registered anywhere. The README
  advertises the two sagas "side by side, for comparison" and
  `docs/saga/milestone-75-saga-mode-both-by-default.md` is titled
  "`Saga:Mode=Both` Is the Default Now" — both now inaccurate. Either add a
  Compose profile that runs `Both` (so the comparison is reproducible, which is
  the point of the milestone) or retitle the doc and mark the choreographed path
  as reference-only.
- **`BuildingBlocks.Contracts/CatalogClientOptions.cs`** contains only a
  three-line comment explaining that the real class lives in
  `BuildingBlocks.HttpClients`. Delete the file; the explanation belongs in the
  class that survived.
- **Two styling systems in the frontend.** `apps/storefront-web/package.json`
  ships MUI 9 + Emotion *and* Tailwind 4 (`postcss.config.js`,
  `src/index.css:6-7`), for a total of six Tailwind `className`s across the whole
  `src/` tree. Pick one — almost certainly MUI, given every component uses it —
  and drop `tailwindcss`, `@tailwindcss/postcss`, `autoprefixer` and `postcss`
  from the build.
- **No PodDisruptionBudget for `payments-service`.** Five workloads have one;
  `payments-service` runs at `replicas: 1` with none, so a node drain takes
  payments down entirely. Either give it `replicas: 2` + a PDB, or document why
  single-replica is acceptable for the service that owns money.
- **No Grafana dashboard for `payments-service` or the saga.**
  `observability/grafana/dashboards/` has cart, catalog, inventory, orders and
  storefront. The two most intricate subsystems in the repo have none.

---

## Implementation plan

Sequenced so that each phase leaves the system in a better state on its own,
and so the structural fixes land before the guardrails that depend on them.

### Phase 1 — Stop the bleeding (1 session)

Small, independent, high-value. No design decisions required.

| Task | Finding | Files |
|---|---|---|
| `ignoreDifferences` for HPA/KEDA-managed replicas; drop hardcoded `replicas` | 1 | `kubernetes/argocd/application.yaml`, `orders-api.yaml`, `orders-worker.yaml` |
| Add saga consumer groups to the KEDA triggers | 11 | `orders-worker-scaledobject.yaml` |
| Reorder CI: build → scan → sign → push | 10a | `.github/workflows/ci.yml` |
| Restore `RecordIdempotentReplay()`; delete the dead bypass counter | 12 | `EfOrderRepository.cs`, `OrdersTelemetry.cs` |
| Unify Compose image tags to `:dev`; add `--build` to the quickstart | 13 | `compose/compose.yaml`, `README.md` |
| Delete the placeholder `CatalogClientOptions.cs` | 21 | `BuildingBlocks.Contracts/` |

**Done when:** a load run shows replica counts holding where the autoscaler put
them across an Argo sync, and CI fails *before* publishing on a seeded CVE.

### Phase 2 — Close the recurring bug class (1–2 sessions)

The one that stops paying interest.

1. `KafkaConnectionOptions` + fill-down `PostConfigure` (finding 3). Delete the
   9 redundant `*__BootstrapServers` from Compose and Kubernetes.
2. Templatize the KEDA `bootstrapServers` from the same ConfigMap.
3. `scripts/ci/verify-config-parity.sh` + CI job, covering Deployments *and*
   migration/seed Jobs (finding 5).
4. Single-source `Saga:Mode` via ConfigMap / `.env` (finding 18).

**Done when:** deleting `Kafka__BootstrapServers` from one Kubernetes manifest
fails CI with a named message, not at pod startup.

### Phase 3 — Make resilience real (1–2 sessions)

1. `AddCriticalHttpResilience()` / `AddBestEffortHttpResilience()` in
   `BuildingBlocks.Resilience`; migrate all 9 call sites; remove the outer
   `HttpClient.Timeout` (finding 2).
2. Split readiness from dependency health across all seven services; one
   documented rule (finding 8).
3. Move the distributed rate limiter to `BuildingBlocks`; mount it in
   Storefront and Cart (finding 9).
4. `MaxPollIntervalMs` + the cumulative-retry-budget validation (finding 20).

**Done when:** a Toxiproxy run against the catalog client shows a retry
*completing* — measurable in the `Polly` meter, which is already exported.

### Phase 4 — Alert on what is already measured (1 session)

1. `observability/prometheus/rules/correctness-invariants.yml` — the seven
   alerts in finding 4.
2. Alertmanager routes: page for divergence/reconciliation/rate-limit-failed-open,
   ticket for the rest.
3. Prove each one fires using the existing `scripts/live-proofs/` and
   `scripts/chaos/` drills; record the results in a milestone doc, per this
   repo's own convention.
4. Grafana dashboards for `payments-service` and the saga (finding 21).

**Done when:** every alert in the new file has a script that provokes it and a
screenshot of it firing.

### Phase 5 — Consolidate the duplicated patterns (2 sessions)

1. `InboxStore` → `BuildingBlocks.Persistence`; replace the 8 inline SQL copies;
   support the ambient-transaction case Inventory/Payments need (finding 6).
2. Fitness function: no `INSERT INTO inbox_messages` literal outside
   `BuildingBlocks.Persistence`.
3. Collapse the 10 dead-letter publisher classes into direct construction at the
   consumer registration sites (finding 15).

**Done when:** `dotnet build` is green, the DLQ integration tests pass unchanged,
and ~350 lines are gone.

### Phase 6 — Close the security gaps (1–2 sessions)

1. `Server` + `AuthorizationPolicy` for the four unprotected services;
   `from:` selectors and egress rules on the NetworkPolicies (finding 7).
2. `orders-worker-client` in Keycloak with its own least-privilege roles
   (finding 14).
3. Kyverno policy validation job in CI (finding 10b).
4. Move `cluster-policies/` under Argo CD, or add a `kubectl diff` CI check.
5. PDB for `payments-service` (finding 21).

**Done when:** an unauthorized in-cluster call to `cart-service` is refused by
the mesh, not just by the JWT layer — demonstrated live, as Milestone 26 did for
`orders-api`.

### Phase 7 — Structural (optional, 2–3 sessions)

Real design work; worth doing only if the lab is going to keep growing.

1. Split `orders-worker` into saga / projection / maintenance roles behind a
   `Workload:Role` setting (finding 11).
2. Decide the gRPC endpoint's fate: give it the anti-entropy caller, or remove
   it and its port (finding 16).
3. Compaction or explicit retention on the event-carrying topics; state the
   replay window (finding 17).
4. `CorrelationIdMiddleware` in `BuildingBlocks`; header allowlist in the BFF
   (finding 19).
5. Drop Tailwind from the frontend build (finding 21).

---

## What is genuinely solid

Worth stating plainly, because the list above is long and the findings are
structural rather than broken:

- **The outbox is textbook.** `OutboxPublisher<TDbContext>`'s claim-window
  design — commit the claim, publish outside any transaction, let a crashed
  claim expire into a redelivery — is correct, and its doc comment explains the
  ordering contract it does *and does not* provide, which is rarer than the
  implementation.
- **`KafkaConsumerHost`'s delegate-based extraction** is the right call. It
  collapsed six near-identical consume/retry/seek/DLQ/commit loops without
  forcing an interface hierarchy on six unrelated processors, and it preserved
  the per-consumer logger categories so existing dashboards kept working.
- **The distinction between infrastructure faults and processing faults** is
  drawn consistently and correctly: infra faults never dead-letter, they leave
  the offset uncommitted and seek back. That single decision is why this system
  does not lose messages during a database blip.
- **The advisory-lock discipline in Inventory.** `SkuAdvisoryLock`'s doc comment
  describes the exact interleaving that motivated pulling it out of one class
  and sharing it with the sweeper. That is the level of rigour the whole
  codebase is written at.
- **Domain fitness functions across all seven services**
  (`Services.ArchitectureTests`) enforce at the namespace level what project
  boundaries enforce for Orders, and the exclusions are argued rather than
  assumed.
- **The comments explain trade-offs, not mechanics.** Nearly every finding above
  was found *because* a comment stated a constraint precisely enough to check —
  the Kyverno scope note, the `Saga__Mode` match requirement, the
  `orders-api-clients` reuse. A codebase that documents its own compromises this
  honestly is one an audit can actually make progress on.
