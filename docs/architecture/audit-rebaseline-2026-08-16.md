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
| 7 | Linkerd mesh authorization (`Server`/`AuthorizationPolicy`) covers only `orders-api`; the other six workloads have none | **Open** - only `kubernetes/cluster-policies/orders-api-authz.yaml` exists |
| 10b | Kyverno policy validation as a CI job (not just applied to the live cluster) | **Open** |
| 11 | `orders-worker` running eight independent workloads (saga orchestrator, both sweepers, projection processor, anti-entropy, ...) in one process/lifecycle | **Open** - structural, optional (Phase 7 in that audit) |
| 14 | `orders-worker` borrows `orders-api`'s Keycloak client credentials instead of its own least-privilege client | **Open** |
| 16 | The gRPC `OrderQuery` service has no client anywhere in the repo | **Open** - `OrderQueryGrpcService.cs` still exists, unconsumed |
| 17 | Kafka topics retain 24h (`retention.ms=86400000`) under a "durable event log" framing, no stated replay window | **Open** - confirmed unchanged in `compose/compose.yaml` |
| 19 | `CorrelationIdMiddleware` lives in `Orders.Api`, not `BuildingBlocks`, so no other service shares it | **Open** (the middleware). The header-allowlist half of this finding (BFF forwarding `X-Correlation-ID`/`Idempotency-Key`/`Accept`) is now closed - see the 08-16 audit's Phase 3 |
| 21a | `Saga__Mode` doc/behavior mismatch (README says "side by side," code runs `Orchestration` only) | **Open** - `Saga__Mode: Orchestration` confirmed as the only configured value in `compose/compose.yaml` and `kubernetes/base/` |
| 21b | Placeholder `BuildingBlocks.Contracts/CatalogClientOptions.cs` (comment-only file, real class moved to `BuildingBlocks.HttpClients`) | **Open** - file still exists |
| 21c | Two styling systems in the frontend (MUI + Tailwind, six Tailwind classNames total) | **Open** - `tailwindcss` still in `apps/storefront-web/package.json` |
| 21d | No Grafana dashboard for `payments-service` or the saga | **Closed** - `payments-overview.json` and `saga-overview.json` both exist under `observability/grafana/dashboards/` |
| 21e (PDB) | No PodDisruptionBudget for `payments-service` | **Closed via the documented-exception branch**, not the replicas:2 branch - the finding's own acceptance criteria allowed either "give it replicas:2 + a PDB, or document why single-replica is acceptable." The 08-16 audit chose and implemented the second: `kubernetes/base/payments-service.yaml` now carries an explicit comment explaining why a PDB would block every node drain given `replicas:1`/`strategy:Recreate`. |

## What this means for the roadmap's Phase 2+ work

Findings 16 (gRPC client fate) and 19 (shared correlation middleware) overlap
directly with roadmap Phase 2/3 items already in scope. The rest (7, 10b, 11,
14, 17, 21a-c) are real but were out of scope for the 08-16 pass and are
carried forward here rather than re-discovered later - see the roadmap's
`Executable backlog order` for where each lands.
