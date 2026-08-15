# Build, Runtime and Time-Handling Review — Implementation Plan (2026-08-15)

Fourth audit in the current series. The first three covered, in order:

- [`audit-2026-08-14-service-and-business-rule-review.md`](audit-2026-08-14-service-and-business-rule-review.md)
  — business rules across Orders/Payments/Inventory/Cart/Storefront (13
  findings, closed in `84cd877`).
- [`audit-2026-08-15-frontend-catalog-infra-review.md`](audit-2026-08-15-frontend-catalog-infra-review.md)
  — the React frontend, `Catalog.Service`, deployment manifests.
- [`audit-2026-08-15-architecture-and-cross-cutting-review.md`](audit-2026-08-15-architecture-and-cross-cutting-review.md)
  — shared building blocks, cross-service contracts, config surface,
  alerting (21 findings; Phases 1–5 closed in `6118979`, Phases 6–7 open).

All three read C# source, YAML manifests, and CI config. **None of them
looked at the container build layer, the runtime environment those
containers actually start with, or how the codebase handles time** — three
areas where this system's behaviour is decided outside any `.cs` file that
an audit reading application code would ever open.

**Method.** I read all 7 Dockerfiles line by line, diffed the runtime `ENV`
they set against what the docs claim gates it, diffed `.dockerignore`
against what is actually on disk in a working tree, and then traced every
`DateTimeOffset.UtcNow` call site against the `TimeProvider` singleton that
every service registers. Findings are ranked; the first three are worth
acting on before the rest.

**Standing note on the previous pass.** CI for `6118979` is green on
`secrets scan`, `complexity and module size`, `build and test` (including
the Testcontainers integration suite), `known-CVE NuGet dependencies`, and
the frontend job. Finding 1 below is partly a consequence of a change made
in that commit, and is called out as such.

---

## Executive summary

| # | Finding | Severity | Theme |
|---|---|---|---|
| 1 | `node_modules` (387 MB) is not in `.dockerignore` and overwrites the container's own install | P1 | Build |
| 2 | The CLR profiler attaches unconditionally; the documented gate does not gate it | P1 | Runtime |
| 3 | `TimeProvider` is registered everywhere and bypassed in 22 files, including every deadline sweeper | P1 | Correctness |
| 4 | Seven Dockerfiles duplicate ~50 lines of `COPY` boilerplate each | P2 | Build |
| 5 | `.dockerignore` ships `docs/`, `output/`, `iac/`, `artifacts/` into every build context | P2 | Build |
| 6 | Carried over and still open: no Grafana dashboard for `payments-service` or the saga | P2 | Observability |
| 7 | `k3s-build-images.sh`'s comment describes tags that no longer exist | P3 | Cleanup |
| 8 | Carried over and still open: `docs/README.md` states a saga default that is not the default | P3 | Docs |

---

## 1. `node_modules` is not in `.dockerignore` and overwrites the container's own install (P1)

`apps/storefront-web/node_modules` is **387 MB** on a working tree. It is
correctly ignored by git (`apps/storefront-web/.gitignore:10`), but
`.dockerignore` is a separate mechanism that does not read `.gitignore`,
and the repository-root `.dockerignore` never mentions it:

```
.git
.gitignore
.idea
.vscode
**/bin
**/obj
**/TestResults
compose/.env
data
kubernetes
observability
```

`apps/src/Storefront.Service/Dockerfile` then does the standard
install-then-copy sequence:

```dockerfile
COPY apps/storefront-web/package.json apps/storefront-web/package-lock.json ./
RUN npm ci
COPY apps/storefront-web/ ./     # <- host node_modules lands on top of the container's
```

The second `COPY` copies the host's `node_modules` over the one `npm ci`
just built inside the image. On a macOS/Apple-Silicon workstation — this
repo's stated primary development machine — those are darwin-arm64 native
binaries (`esbuild`, `rollup`, `lightningcss`) being written into a Linux
image. The host's stale `dist/` gets copied in the same way.

**Why this has not bitten yet, and why it is about to.** CI checks out a
clean tree with no `node_modules`, so the GitHub Actions path is unaffected
— which is exactly why the frontend job is green. The path that *is*
affected is a local `docker compose build`, and commit `6118979` added
`--build` to the README quickstart specifically to stop Compose serving
stale images. That change made the broken path the documented default. This
finding is the direct consequence, and it should be fixed in the same spirit
the `--build` change was made.

The 387 MB is also uploaded into the build context for **all seven**
images, not just Storefront's, because every service builds with
`context: ..` (Compose) or `context: .` (CI).

**Fix.** Add to `.dockerignore`:

```
**/node_modules
apps/storefront-web/dist
```

Then verify with a build that the frontend still compiles from a clean
context: `docker compose --profile compose-apps build storefront-service`.
Worth a one-line assertion in `scripts/ci/verify-config-parity.sh`'s
neighbourhood — or simply a comment in `.dockerignore` naming why
`node_modules` is listed, since the failure it prevents is invisible until
someone builds locally with a populated working tree.

---

## 2. The CLR profiler attaches unconditionally; the documented gate does not gate it (P1)

Every one of the seven Dockerfiles ends with the same runtime block:

```dockerfile
ENV ASPNETCORE_URLS=http://+:8080 \
    ...
    CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/opt/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/opt/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/opt
```

`docs/architecture/continuous-profiling.md:11` states the design intent:

> activation is entirely environment-variable driven, gated behind
> `PYROSCOPE_PROFILING_ENABLED` at deploy time so it isn't unconditionally
> forced on.

**These are two different variables at two different layers, and the one
that is gated is not the one that decides whether the profiler attaches.**

- `CORECLR_ENABLE_PROFILING` is a CoreCLR *runtime* switch. Set to `1`, the
  runtime loads the native profiler at `CORECLR_PROFILER_PATH` during
  process startup, before any managed code runs. It is baked into the image
  `ENV` and nothing overrides it anywhere.
- `PYROSCOPE_PROFILING_ENABLED` is Pyroscope's own variable, read by the
  already-loaded profiler. `kubernetes/base/*.yaml` sets it on six
  workloads; `compose/compose.yaml` sets it on none — and its own comment
  at the `pyroscope` service says "no Compose service instruments against
  it."

So in Docker Compose — the README quickstart, and the environment every k6
profile, the tail-latency benchmark, and `memory-leak-check.sh` run
against — all seven services start with a native CLR profiler attached and
`LD_PRELOAD` injecting a shim into every process, while the documentation
says profiling "isn't unconditionally forced on."

The 1-core CPU floor in `kubernetes/base/orders-api.yaml:178-181` is
corroborating evidence that attach happens at startup independent of intent:

> Pyroscope's native profiler refuses to start below a 1-core CPU limit
> ("CPU limit is too low for the profiler to work properly" — logged at
> container startup)

**What I have not verified, stated plainly.** Whether the Pyroscope
profiler, once loaded, checks `PYROSCOPE_PROFILING_ENABLED` and returns a
failure HRESULT from `Initialize` to make the CLR detach it cleanly. If it
does, steady-state overhead is near zero and the practical impact is
limited to startup. If it does not, every Compose-based measurement in this
repo was taken with a profiler attached. **Either way the documented gate is
not the mechanism doing the gating**, and the repo's own standard — measure,
don't assume — is not being met here.

**Fix**, in two steps:

1. **Verify, cheaply.** Bring up Compose and check both the environment and
   the profiler's own startup log:
   ```bash
   docker compose --profile compose-apps exec orders-api-1 env | grep CORECLR
   docker compose --profile compose-apps logs orders-api-1 | grep -i pyroscope
   ```
   Record the result in `docs/architecture/continuous-profiling.md` — this
   is exactly the kind of "what actually happened" measurement every
   milestone report in this repo already carries.
2. **Make the gate real.** Move the four profiler variables out of the
   Dockerfile `ENV` and into `kubernetes/base/*.yaml` alongside the
   `PYROSCOPE_PROFILING_ENABLED` they are supposed to travel with, so an
   image with no profiling configuration genuinely runs without a profiler.
   The `.so` files stay baked into the image (that part of the design is
   sound — they cannot come from `dotnet publish`); only the attach
   variables move to where the deploy-time decision is made.

Re-run one k6 profile before and after to quantify what, if anything, the
attach was costing. That number belongs in the milestone doc.

---

## 3. `TimeProvider` is registered everywhere and bypassed in 22 files (P1)

Every service registers `builder.Services.AddSingleton(TimeProvider.System)`,
and 16 files inject it properly — `CartStore`, `OrderPricingService`,
`PaymentSettlementProcessor`, `InventoryReservationMessageProcessor`,
`SagaTimeoutSweeper`, `OrderSagaOrchestrator`, and others. The intent is
unambiguous and the pattern is already established.

Twenty-two files call `DateTimeOffset.UtcNow` directly instead, including
**every component whose entire job is a deadline**:

| File | What the clock decides there |
|---|---|
| `Payments.Service/PaymentAuthorizationSweeper.cs` | when a card authorization has expired |
| `Inventory.Service/BackorderTimeoutSweeper.cs` | when a backorder has timed out |
| `Inventory.Service/PurchaseOrderReceivingSweeper.cs` | when replenishment lead time has elapsed |
| `BuildingBlocks.Persistence/RetentionSweeper.cs` | what falls outside the retention window |
| `BuildingBlocks.Persistence/OutboxPublisher.cs` | claim windows and retry backoff |
| `Orders.Infrastructure/RateLimiting/RedisSlidingWindowRateLimiter.cs` | the sliding window itself |
| `Orders.Application/UseCases/ReturnOrder/ReturnOrderHandler.cs` | **whether the customer's shipping is refunded** |

The last one is demonstrable, so here it is in full rather than as an
assertion.

`ReturnOrderHandler.cs:63-68` passes the ambient clock into the domain:

```csharp
var (orderReturn, rejection, offendingSku) = order.TryReturn(
    ..., TimeSpan.FromDays(returnOptions.Value.RegretWindowDays),
    DateTimeOffset.UtcNow);
```

The domain itself is written correctly — `OrderReturn.IsOwed` takes time as
a parameter and has no ambient dependency at all:

```csharp
ReturnReasonCategory.Regret => requestedAt - orderCreatedAt <= regretWindow,
```

That boolean decides whether the shopper gets their shipping cost back. The
handler is the only thing standing between a testable domain rule and a
wall clock.

**And the test proves the gap rather than covering it.**
`tests/Orders.UnitTests/ReturnOrderHandlerTests.cs:20` pins a fixed clock:

```csharp
private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
...
customerId, "BRL", Now.AddDays(-10), ...   // order placed 2026-07-31
```

The handler ignores it and calls `DateTimeOffset.UtcNow`. So the test
intends a 10-day-old order and actually exercises a *15-day-old* one today
— a gap that widens by one day, every day, forever. With a 7-day
`RegretWindowDays`, the `IsOwed` branch is permanently pinned to `false`.
The tests still pass because they assert `ReturnRejectionReason.None` and
`SaveCalled`, which hold on both sides of the window; **the shipping-refund
amount is never asserted, and the inside-the-window branch cannot be reached
from a test as the code stands.**

This also quietly undercuts two things the repo advertises: the
deterministic simulation testing of Milestone 58, and
`scripts/live-proofs/clock-skew-saga-timeout-test.sh` — neither can control
time for any component in the table above.

**Fix**, smallest useful slice first:

1. Inject `TimeProvider` into `ReturnOrderHandler` and pass
   `timeProvider.GetUtcNow()` into `TryReturn`. Then extend
   `ReturnOrderHandlerTests` with the two cases that matter — a return
   inside the regret window (shipping refunded) and one outside it (not
   refunded) — asserting the refund amount, driven by a
   `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`).
2. Do the same for the four sweepers, whose tests currently need real
   elapsed time or a pre-dated fixture row to exercise a timeout at all.
   `BackorderTimeoutSweeperTests` and `SagaTimeoutSweeperTests` both exist
   and both get simpler.
3. Leave `OutboxPublisher`, `RetentionSweeper` and the rate limiter for a
   follow-up — they are infrastructure with wider blast radius, and the
   business-rule sites above are where a wrong answer costs money.
4. Add a fitness function in `Services.ArchitectureTests` asserting no
   `DateTimeOffset.UtcNow` in `*.Application` / `*Sweeper` / `*Handler`
   types, so the next handler is written the way the other sixteen already
   are.

---

## 4. Seven Dockerfiles duplicate ~50 lines of `COPY` boilerplate each (P2)

Each service Dockerfile lists every `BuildingBlocks.*` project twice — once
for the `.csproj` (restore-layer caching) and once for the sources — then
restores and publishes. The blocks are identical apart from which projects
appear and which `.csproj` is the entry point.

This has already failed once, in this repo's own history:

```
cfa7117 fix(ci): include shared HTTP clients in container builds
```

That commit exists because `BuildingBlocks.HttpClients` was added to the
solution and to the `.csproj` references, but not to every Dockerfile that
needed it — the build broke only inside the container, after CI's
`dotnet build` had already gone green. The structure that allowed it is
unchanged: adding an eighth `BuildingBlocks` project today still means
remembering to edit up to seven Dockerfiles by hand.

The audit of `6118979` made this slightly more pressing:
`BuildingBlocks.Persistence` now has a `ProjectReference` to
`BuildingBlocks.Resilience`. That happened to be safe — all seven
Dockerfiles already copied both — but nothing checked it, and nothing would
have.

**Fix.** Two options, in increasing order of effort:

- **Cheap and sufficient:** a CI check that, for each service, every
  `ProjectReference` reachable from its `.csproj` has a matching `COPY` line
  in its Dockerfile. About 30 lines of Python next to
  `verify-config-parity.sh`, and it turns a class of container-only build
  break into a CI failure.
- **Structural:** collapse to a single parameterised Dockerfile
  (`ARG PROJECT=Orders.Api`) that copies `apps/src/` wholesale and lets
  `dotnet restore` resolve the graph. Loses some layer-cache granularity;
  gains one file instead of seven. Worth it only if the service count keeps
  growing.

Recommend the CI check now, and the consolidation only if an eighth service
appears.

---

## 5. `.dockerignore` ships unrelated directories into every build context (P2)

Beyond `node_modules` (finding 1), the root `.dockerignore` excludes
`kubernetes` and `observability` but not `docs/`, `output/`, `iac/`,
`load-tests/`, `.github/`, or `artifacts/`. Every one of the seven builds
uploads all of them to the daemon.

`output/` currently holds a generated `.docx` and `.pdf`. `artifacts/` is
git-ignored working notes — and on this machine it contains
`artifacts/lab-server.md`, which records the lab server's LAN IP, SSH user,
and notes about which credentials are still placeholders.

To be precise about the risk: **no Dockerfile copies any of these into an
image** — every `COPY` names an explicit `apps/src/...` path, so nothing
leaks into a published layer. The exposure is that the files are transferred
into the build context and can be retained in local BuildKit cache. In CI
the directory does not exist at all (git-ignored, never checked out). So
this is build hygiene and a small local-only exposure, not a published-image
leak.

**Fix.** Extend `.dockerignore`:

```
**/node_modules
apps/storefront-web/dist
artifacts
docs
output
iac
load-tests
.github
scripts
```

Keep `compose/` out of the exclusion list — the Storefront build does not
need it, but excluding it buys nothing and risks a surprise if a future
Dockerfile does.

---

## 6. Carried over: no Grafana dashboard for `payments-service` or the saga (P2)

Raised as item 21 in the previous audit and deferred with Phase 7.
`observability/grafana/dashboards/` still holds five dashboards — cart,
catalog, inventory, orders, storefront. The two most intricate subsystems in
the repo, the payments lifecycle and the four-step saga, have none.

This is now more visible than it was: `6118979` added seven
correctness-invariant alerts, four of which
(`SettlementReconciliationUnresolved`, `OrphanedSagaRepliesHigh`,
`AntiEntropyDivergenceDetected`, `ProjectionLagHigh`) fire on saga and
payments behaviour with no dashboard to open when they do.

**Fix.** Two dashboards, each panelled on metrics that already exist:
`payments_decided_total`, `payments_settlement_reconciliation_unresolved_total`,
`saga_orphaned_reply_total`, `orders_projection_lag_ms`, plus the standard
consumer-lag and DLQ panels the other five dashboards already use.

---

## 7. `k3s-build-images.sh`'s comment describes tags that no longer exist (P3)

Introduced by `6118979`. The script reads:

```bash
# Each service's compose-declared image tag drifts independently (whatever
# milestone last touched that Dockerfile - e.g. milestone-41-inventory,
# milestone-42-by-sku), so it's read back from `compose config` rather than
# assumed, and retagged to the one fixed tag the K3s overlay expects.
```

Those tags were replaced with a uniform `:dev` in that same commit. The
*code* is still correct — reading the tag back from `compose config` is
robust either way, and is why nothing broke — but the comment now explains a
problem that no longer exists, which is precisely the kind of drift the rest
of this codebase's comments are unusually good at avoiding.

**Fix.** Rewrite the comment to say the tag is read back rather than
hardcoded so the two files cannot drift, and drop the milestone examples.

---

## 8. Carried over: `docs/README.md` states a saga default that is not the default (P3)

`docs/README.md:36` still lists:

> Milestone 75: `Saga:Mode=Both` Is the Default Now, Not Choreography

The code default is `SagaMode.Orchestration`
(`Orders.Worker/Program.cs:208`), and `6118979` consolidated both Compose
and Kubernetes onto a single `Orchestration` value. The milestone report
records what was true when written; the index entry presents it as current.

**Fix.** Retitle the index entry (the milestone document itself is a dated
historical record and should not be rewritten), or add a one-line "superseded
by" note pointing at the current default.

---

## Implementation plan

### Phase 1 — Unblock the local build path (1 session)

Small, mechanical, and it protects the quickstart that `6118979` just
changed.

| Task | Finding | Files |
|---|---|---|
| Add `**/node_modules`, `dist` to `.dockerignore` | 1 | `.dockerignore` |
| Add the remaining directory exclusions | 5 | `.dockerignore` |
| Fix the stale tag comment | 7 | `scripts/infra/k3s-build-images.sh` |
| Retitle the milestone-75 index entry | 8 | `docs/README.md` |

**Done when:** `docker compose --profile compose-apps build storefront-service`
succeeds from a working tree with a populated `node_modules`, and the build
context transferred is visibly smaller in the build output.

### Phase 2 — Make the profiler gate real (1 session)

1. Verify empirically whether the profiler attaches and whether it detaches
   itself under Compose (finding 2, step 1). Record the measurement.
2. Move `CORECLR_*` / `LD_PRELOAD` / `LD_LIBRARY_PATH` out of the seven
   Dockerfile `ENV` blocks into `kubernetes/base/*.yaml`.
3. Re-run one k6 profile before and after; put the delta in
   `docs/architecture/continuous-profiling.md`.

**Done when:** `docker compose exec <svc> env | grep CORECLR` returns
nothing, the K3s pods still report profiles in Pyroscope, and the doc states
a measured overhead rather than an assumed gate.

### Phase 3 — Put time back under control (1–2 sessions)

1. `ReturnOrderHandler` takes `TimeProvider`; add the two regret-window
   tests asserting the shipping-refund amount on both sides of the boundary.
2. The four deadline sweepers take `TimeProvider`; simplify
   `BackorderTimeoutSweeperTests` / `SagaTimeoutSweeperTests` to drive it.
3. Architecture fitness function banning `DateTimeOffset.UtcNow` in
   `*Handler` / `*Sweeper` types.
4. Leave `OutboxPublisher`, `RetentionSweeper`, the rate limiter, and the
   `Program.cs`/seeder call sites as a documented follow-up.

**Done when:** a test can place an order, advance a fake clock past the
regret window, and assert the shipping refund changes — today impossible.

### Phase 4 — Guardrails and the carried-over dashboard gap (1 session)

1. Dockerfile ↔ `ProjectReference` parity check in CI (finding 4).
2. Payments and saga Grafana dashboards (finding 6).

**Done when:** deleting a `COPY` line from one Dockerfile fails CI with a
named message, and each of the four saga/payments alerts added in `6118979`
has a dashboard to link to.

---

## What is genuinely solid

- **The Dockerfiles get the hard parts right.** Non-root (`USER $APP_UID`),
  pinned SDK digest-by-version, `--no-restore` on publish, restore-layer
  caching ordered before source copy, `UseAppHost=false`, and a
  `HEALTHCHECK` that hits the same readiness endpoint Kubernetes probes.
  The problems above are additive mistakes, not a weak foundation.
- **`OrderReturn.IsOwed` is exactly right.** It takes `requestedAt` as a
  parameter and has no ambient clock dependency — which is the *only*
  reason finding 3 is a one-line handler fix rather than a domain rewrite.
  The same is true of `Order.TryReturn` and `ShippingRefundPolicy`.
- **No sync-over-async, no `async void`, no empty catches, no
  `TODO`/`HACK`/`FIXME` anywhere in `apps/src`.** I swept for all of them;
  the CI gates that enforce two of these are doing their job, and the
  others hold without a gate.
- **`.gitignore` hygiene is correct** — `node_modules` and `dist` are
  properly ignored by git. Finding 1 exists only because `.dockerignore` is
  a separate file that does not inherit from it, which is a Docker design
  wart rather than a lapse here.
- **The `TimeProvider` pattern is already established in 16 files**,
  including the hardest ones (`CartStore`'s CRDT merge,
  `OrderPricingService`, `PaymentSettlementProcessor`). Finding 3 is
  finishing a migration that is well underway, not starting one.
