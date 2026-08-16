# Audit Rebaseline (2026-08-16)

Phase 1, task 1 of `implementation-roadmap.md`: every finding across the audit
series, classified against the current `main` HEAD rather than assumed from
the audit docs themselves. Executable evidence (a commit, a passing test, a
file that still or no longer exists) wins over what an older audit claimed.

## Audit-level status

| Audit | Closed by | Status |
|---|---|---|
| [08-14 service and business rules](audit-2026-08-14-service-and-business-rule-review.md) | `84cd877` | Closed |
| [08-15 build/runtime/time](audit-2026-08-15-build-runtime-and-time-handling-review.md) | `f839dd5` | Closed |
| [08-15 domain and business rules](audit-2026-08-15-domain-and-business-rules-review.md) | `8ce17ec` | Closed |
| [08-15 patterns and API layer](audit-2026-08-15-patterns-and-api-layer-review.md) | `567dc02` | Closed |
| [08-15 frontend/catalog/infra](audit-2026-08-15-frontend-catalog-infra-review.md) | (loose-ends commits below) | Closed |
| [roadmap 91-99](../roadmap-milestones-91-99.md) | `77feb39` | Closed |
| [08-16 loose ends, business rules, DevOps](audit-2026-08-16-loose-ends-business-rules-and-devops-review.md) | `404e1be`, `3b0d00e` | Closed - all 6 phases implemented and verified (build green, 588+ local tests) |
| [08-15 architecture and cross-cutting](audit-2026-08-15-architecture-and-cross-cutting-review.md) | `6118979` (Phases 1-5 only) | **Partially open** - see below |

## Architecture-and-cross-cutting: the open remainder

Phases 1-5 closed in `6118979`. Phases 6-7 were never attempted (that audit's
own table already said so). Re-verified against HEAD, finding by finding:

| Finding | Description | Status |
|---|---|---|
| 7 | Linkerd mesh authorization (`Server`/`AuthorizationPolicy`) covers only `orders-api`; the other six workloads have none | **Implemented; lab proof pending** - `kubernetes/base/mesh-authorization.yaml` adds named caller identities and node-only operational access for the other six HTTP workloads; the staging smoke gate must prove the live mesh paths before promotion |
| 10b | Kyverno policy validation as a CI job (not just applied to the live cluster) | **Closed** - the pinned Kyverno CLI runs `kubernetes/cluster-policies/tests` in the blocking CI quality job; the three immutable/local-image cases also pass with Kyverno CLI 1.16.1 locally |
| 11 | `orders-worker` running eight independent workloads (saga orchestrator, both sweepers, projection processor, anti-entropy, ...) in one process/lifecycle | **Accepted risk** - optional structural split deferred until independent scaling or failure-rate evidence justifies the extra deployment/coordination surface; health, graceful shutdown and terminal-state alerting remain required |
| 14 | `orders-worker` borrows `orders-api`'s Keycloak client credentials instead of its own least-privilege client | **Closed** - Compose/Kubernetes use the dedicated `orders-worker` client and the realm bootstrap grants only `inventory:read` and `payments:read` |
| 16 | The gRPC `OrderQuery` service has no client anywhere in the repo | **Open** - `OrderQueryGrpcService.cs` still exists, unconsumed |
| 17 | Kafka topics retain 24h (`retention.ms=86400000`) under a "durable event log" framing, no stated replay window | **Closed** - business/CDC topics retain seven days, DLQs 30 days, aligned with the database evidence window and documented in `docs/data/milestone-63-outbox-inbox-retention.md` |
| 19 | `CorrelationIdMiddleware` lives in `Orders.Api`, not `BuildingBlocks`, so no other service shares it | **Closed** - the bounded shared middleware lives in `BuildingBlocks.Observability`, is installed by all HTTP services and has focused replacement/propagation tests |
| 21a | `Saga__Mode` doc/behavior mismatch (README says "side by side," code runs `Orchestration` only) | **Closed** - README now states that both implementations exist while the deployed default is orchestration |
| 21b | Placeholder `BuildingBlocks.Contracts/CatalogClientOptions.cs` (comment-only file, real class moved to `BuildingBlocks.HttpClients`) | **Closed** - the placeholder is absent and the real option remains in `BuildingBlocks.HttpClients` |
| 21c | Two styling systems in the frontend (MUI + Tailwind, six Tailwind classNames total) | **Accepted risk** - MUI owns components/reset; Tailwind supplies only a small utility layer and explicitly omits preflight, documented in `apps/storefront-web/src/index.css` |
| 21d | No Grafana dashboard for `payments-service` or the saga | **Closed** - `payments-overview.json` and `saga-overview.json` both exist under `observability/grafana/dashboards/` |
| 21e (PDB) | No PodDisruptionBudget for `payments-service` | **Closed via the documented-exception branch**, not the replicas:2 branch - the finding's own acceptance criteria allowed either "give it replicas:2 + a PDB, or document why single-replica is acceptable." The 08-16 audit chose and implemented the second: `kubernetes/base/payments-service.yaml` now carries an explicit comment explaining why a PDB would block every node drain given `replicas:1`/`strategy:Recreate`. |

## What this means for the roadmap's Phase 2+ work

Finding 16 (the unconsumed gRPC query surface) remains the only unresolved
code-level item from this table. It is not removed without an explicit contract
deprecation decision. Finding 7 is implemented in manifests but remains
acceptance-blocked until the lab staging smoke proves every allowed path and a
denied identity. Finding 11 and 21c are deliberate, documented risks rather
than silently forgotten cleanup work.
