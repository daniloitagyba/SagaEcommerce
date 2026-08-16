# SagaEcommerce Phased Implementation Roadmap

## Purpose

This roadmap converts the repository audits into small, verifiable delivery
phases. It is an implementation plan, not a claim that every audit finding is
still open. Before starting each item, re-check the referenced evidence against
the current branch because several fixes may already be in progress.

The plan follows four rules:

1. P0 and P1 correctness risks precede structural refactoring.
2. Every item must leave the repository buildable and deployable.
3. A capability is complete only when tests, telemetry and operational evidence
   exist for its critical failure modes.
4. Thresholds are measured against the repository baseline before they become
   blocking. Quality gates must detect regression, not create permanent noise.

## Evidence base and current baseline

The main inputs are the reviews under `docs/architecture/audit-2026-08-14-*`,
`docs/architecture/audit-2026-08-15-*` and
`docs/architecture/audit-2026-08-16-*`, plus the live guardrails in
`.github/workflows/ci.yml`. When an audit conflicts with executable code or a
newer test, executable evidence wins and the audit should be marked superseded.

Protections already present include nullable references, warnings as errors,
.NET and threading analyzers, architecture tests, unit/integration/contract
tests, frontend lint/type-check/tests, coverage, mutation testing, complexity
and module-size budgets, Gitleaks, NuGet audit, CodeQL, Trivy, SBOM generation
and keyless image signing.

The implemented roadmap adds the following blocking repository-quality checks
to the existing CI:

- `dotnet format --verify-no-changes` after the solution restore;
- `actionlint` for GitHub Actions syntax and expression validation;
- `bash -n` for every shell script under `scripts/` and `compose/`;
- calibrated ShellCheck and Hadolint analysis;
- Kustomize rendering followed by strict Kubeconform validation of standard
  Kubernetes resources. CRDs without a published schema are reported as
  skipped, not silently treated as valid;
- Kyverno policy tests for immutable-image admission behavior;
- Prometheus rule/config validation and Grafana JSON validation;
- npm direct/transitive vulnerability audit at high severity.

## Implementation status (2026-08-16)

| Phase | Repository status | Evidence and remaining acceptance |
| --- | --- | --- |
| 1 — Stabilization | Implemented | The [audit rebaseline](audit-rebaseline-2026-08-16.md) links the current evidence; immutable runtime pins/rescans, warehouse-safe replenishment and bounded resilience paths are present. Release build and local tests pass; remote integration/Trivy execution remains a CI/lab gate. |
| 2 — Domain and architecture | Implemented | Promotion calendar/budget, centralized transitions, exact money contracts and architecture fitness functions are executable. [ADR-001](adr-001-microservices-vs-modular-monolith.md) records the boundary decision. |
| 3 — Distributed reliability | Implemented | The [producer/consumer reliability inventory](reliability-inventory-2026-08-16.md) records outbox/inbox, IDs, versions and replay guarantees. Duplicate/order/restart proofs remain required in the remote integration job. |
| 4 — Quality and security | Implemented | CI is split into fast and Testcontainers jobs, third-party actions are SHA-pinned, Dependabot covers Actions/NuGet/npm/Docker, ShellCheck/Hadolint/npm audit are blocking, request bodies are bounded and the [critical-flow matrix](../testing/critical-flow-matrix.md) names the automated evidence. |
| 5 — Operations | Implemented; scheduled proof pending | Payments/saga dashboards already exist; `commerce-slo.yml` adds checkout/payment/inventory SLOs, every alert has an owner/runbook, [operations runbooks](../operations/runbooks.md) cover recovery, and scheduled backup/chaos jobs retain machine-readable evidence. A real scheduled restore and controlled burn-rate provocation must still produce lab artifacts. |
| 6 — Delivery and GitOps | Implemented; environment acceptance pending | CI publishes/scans/signs immutable commit images, `promote.yml` promotes verified digests by PR, `staging-smoke.yml` verifies all seven running digests and the critical flow, and production requires matching successful staging evidence. Per-workload service accounts, Linkerd authorization, egress policies, admission tests and overlay validation are declarative. Staging/production credentials, Argo destinations and approval rules are external prerequisites and no production promotion was performed by this implementation. |

### Local verification recorded for this implementation

- `dotnet format --verify-no-changes` passed.
- Release solution build passed with zero warnings and zero errors.
- 591 local .NET unit/architecture tests passed.
- 67 frontend tests passed; lint, type-check/build and `npm audit` passed with
  zero reported vulnerabilities.
- `actionlint`, `bash -n`, ShellCheck, Kustomize rendering, strict Kubeconform
  (67 valid, 25 CRDs skipped, zero invalid), Kyverno CLI tests, Prometheus
  `promtool`, Grafana JSON parsing, Gitleaks and `git diff --check` passed.
- Hadolint, container builds/scans/signatures, Testcontainers, mesh/egress
  smoke, restore and chaos acceptance are intentionally left to CI or the
  remote/lab environment required by `ENVIRONMENT.md`.

## Priority and promotion policy

| Priority | Meaning | Promotion rule |
| --- | --- | --- |
| P0 | Data loss, incorrect money/stock state, security exposure or undeployable runtime | Blocks every release |
| P1 | Required for the first reliable production candidate | Blocks production promotion |
| P2 | Important for scale, operability or maintainability | Must have an owner and target release |
| P3 | Evolutionary improvement | Schedule only when evidence justifies it |

A phase can close only when its acceptance criteria pass in CI and, where the
test needs PostgreSQL, Kafka, Redis, MongoDB, Docker Compose or Kubernetes, on
the remote/lab environment described in `ENVIRONMENT.md`.

## Phase 1 — Stabilization

**Objective:** establish a reproducible baseline and close known P0/P1 defects
before adding architecture.

**Tasks**

1. Re-run the audit backlog against the current branch and classify each finding
   as open, in progress, fixed with evidence, superseded or accepted risk.
2. Complete and prove the inventory replenishment fix so a purchase-order
   receipt cannot credit a warehouse different from the originating request.
3. Complete the resilience-pipeline rollout for Payments and Inventory
   transaction writers without retrying non-idempotent work blindly.
4. Pin runtime container images to immutable patch versions or digests and add a
   scheduled rebuild/rescan policy.
5. Preserve all current build, architecture, unit, contract and integration
   tests; quarantine no failing test without an owner and expiry date.

**Affected areas:** `apps/src/Inventory.Service`, `apps/src/Payments.Service`,
`apps/src/BuildingBlocks.Resilience`, service Dockerfiles, relevant unit and
integration-test projects, and `.github/workflows/`.

**Dependencies:** none for baseline classification; integration proof requires
the remote/lab Testcontainers environment.

**Risks:** retry amplification, double processing and false confidence from
local-only validation.

**Acceptance criteria**

- The solution restores and builds in Release with zero warnings.
- All local unit and architecture tests pass.
- All integration and contract tests pass in the lab.
- Concurrent replenishment proves request/receipt warehouse identity and no
  stock drift.
- The seven application Dockerfiles use a documented immutable base-image
  policy and their resulting images pass Trivy.

**Relative estimate:** medium, two to four implementation sessions.

**Expected result:** a stable baseline on which later refactoring does not hide
existing correctness or supply-chain failures.

## Phase 2 — Domain and architecture

**Objective:** make business ownership and invariants explicit without a
cosmetic repository reorganization.

**Tasks**

1. Implement the promotion calendar, exclusivity groups, deterministic priority
   and budget enforcement as one cohesive Orders/Pricing capability.
2. Centralize order-state transitions behind the aggregate/application ports so
   API, saga and return paths cannot apply different side effects.
3. Close loyalty reversal, backorder queue fairness and risk-query bounding with
   focused domain and integration tests.
4. Replace money represented as floating point in gRPC contracts with an exact,
   backward-compatible representation and a migration plan.
5. Enforce service and Clean Architecture boundaries with architecture tests;
   add a rule only for a dependency direction that is intentionally adopted.
6. Decide, through an ADR, whether the current deployment independence and
   scaling needs justify the microservice boundary. Keep extraction-friendly
   modules if a modular monolith is the more economical near-term choice.

**Affected areas:** `Orders.Domain`, `Orders.Application`,
`Orders.Infrastructure`, `Orders.Api`, `Orders.Worker`, pricing contracts,
Inventory, Payments and `*ArchitectureTests`.

**Dependencies:** Phase 1 baseline and explicit business decisions for promotion
stacking, budget exhaustion, backorder fairness and loyalty reversal.

**Risks:** accidental contract breakage, dual business-rule implementations and
over-engineering abstractions with only one real consumer.

**Acceptance criteria**

- Promotion evaluation is deterministic under time, exclusivity, priority and
  concurrent budget consumption.
- Every order transition has one owner and emits the same durable side effects.
- Money crosses APIs and messages without binary floating-point conversion.
- The dependency graph is enforced by passing architecture tests.
- The microservice/modular-monolith decision and trade-offs are recorded in an
  ADR, with no migration performed solely for aesthetics.

**Relative estimate:** large, four to eight sessions.

**Expected result:** business behavior is testable without a web server or
broker, and external adapters implement rather than own the rules.

## Phase 3 — Distributed reliability

**Objective:** make at-least-once delivery, ordering and recovery behavior
explicit for every money, order and stock flow.

**Tasks**

1. Inventory every producer and consumer against outbox, inbox, message ID,
   correlation/causation ID, contract version and replay support.
2. Finish consolidation on shared Inbox/Outbox components and prohibit ad-hoc
   deduplication SQL outside the persistence building block.
3. Add concurrency and ordering guards to projection updates, returns, stock
   release/fill races and payment settlement/refund paths.
4. Define retry budgets below Kafka poll/session limits, with jitter, DLQ and an
   operator-visible terminal state.
5. Prove restart, duplicate, out-of-order, post-commit/pre-publish and failed
   compensation scenarios using Testcontainers and the existing chaos scripts.
6. Document the actual delivery guarantees: local atomicity, at-least-once
   transport and effectively-once effects only where durable idempotency proves
   them.

**Affected areas:** `BuildingBlocks.Messaging`,
`BuildingBlocks.Persistence`, `Orders.Worker`, Inventory, Payments, contracts,
integration tests and `scripts/chaos/`.

**Dependencies:** stable domain transition ownership from Phase 2.

**Risks:** duplicate payment/refund, overselling, stale projections, poison
messages and retry storms.

**Acceptance criteria**

- Critical consumers have durable deduplication and duplicate-delivery tests.
- The outbox closes the database-commit/publication gap for critical events.
- Out-of-order messages cannot regress aggregate or projection state.
- Failed compensation reaches a DLQ/manual-recovery workflow with an audit
  trail.
- Recovery after process and broker restart is demonstrated in the lab.

**Relative estimate:** large, six to ten sessions.

**Expected result:** distributed failure modes are deterministic, observable and
recoverable rather than dependent on timing luck.

## Phase 4 — Quality and security

**Objective:** turn important engineering and security properties into
repeatable, low-noise gates.

**Tasks**

1. Keep the new formatting, workflow, shell and Kubernetes validation gates
   blocking on every pull request.
2. Split CI feedback into fast unit/architecture validation and
   Testcontainers-backed integration/contract validation while keeping both
   required for production promotion.
3. Add tests to the critical-flow matrix below before raising coverage or
   mutation thresholds. Recalibrate thresholds from measured reports.
4. Calibrate Hadolint and ShellCheck against the current repository, fix real
   findings, document narrow exceptions and then make them blocking.
5. Extend dependency scanning to npm direct/transitive vulnerabilities with a
   documented severity and exception-expiry policy.
6. Add authorization-negative tests, payload limits and abuse/rate-limit tests
   for public write endpoints.
7. Pin third-party GitHub Actions by commit SHA and automate reviewed dependency
   updates.

**Affected areas:** `.github/workflows`, `.editorconfig`, `Directory.Build.props`,
frontend configuration, test projects, Dockerfiles and security documentation.

**Dependencies:** critical paths and contracts stabilized in Phases 1–3.

**Risks:** slow CI, flaky gates, meaningless coverage inflation and permanent
allowlists.

**Acceptance criteria**

- Pull requests cannot merge with formatting, workflow, Bash, manifest, build,
  architecture, unit, secret, dependency or SAST failures.
- Every suppression has a reason, owner and review condition.
- Coverage never decreases below the measured baseline; mutation testing covers
  branching domain logic rather than DI wiring.
- Critical authorization and input-abuse cases have negative tests.
- CI duration and flake rate are measured; recurrent flaky tests are fixed, not
  retried indefinitely.

**Relative estimate:** medium, three to five sessions.

**Expected result:** quality and security regressions fail before artifact
publication with actionable diagnostics.

### Critical-flow test matrix target

| Critical flow | Happy path | Failure | Retry | Idempotency | Concurrency | Compensation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Create order | Required | Required | Required | Required | Required | N/A |
| Reserve stock | Required | Required | Required | Required | Required | Required |
| Process payment | Required | Required | Required | Required | Required | Required |
| Cancel order | Required | Required | Required | Required | Required | Required |
| Release stock | Required | Required | Required | Required | Required | Required |
| Refund payment | Required | Required | Required | Required | Required | Required |

“Required” means a named automated test or lab proof must be linked from the
backlog item. A checked box without executable evidence does not count.

## Phase 5 — Operations

**Objective:** make health, degradation and recovery visible and actionable.

**Tasks**

1. Alert on existing failure metrics, including outbox dead letters, DLQ depth,
   saga timeouts, compensation failures and projection lag.
2. Add focused Payments and end-to-end saga dashboards with RED/USE and business
   signals; avoid dashboards that only repeat infrastructure CPU charts.
3. Define SLI/SLOs and burn-rate alerts for checkout availability, order
   completion latency, payment outcomes and inventory consistency.
4. Schedule backup/restore and selected chaos drills in the lab, retain machine-
   readable evidence and tie each drill to alert verification.
5. Add runbooks for DLQ redrive, stuck saga, failed migration, broker outage,
   restore and rollback; include authority and stop conditions.
6. Review readiness dependencies, graceful shutdown and backpressure against the
   degradation behavior implemented in code.

**Affected areas:** `observability/`, `scripts/backup-drills/`,
`scripts/chaos/`, `scripts/ops/`, health checks and operational docs.

**Dependencies:** telemetry and terminal failure states from Phase 3.

**Risks:** alert fatigue, destructive drills and dashboards without ownership.

**Acceptance criteria**

- Every page-worthy alert links to a tested runbook and has an owner.
- Multi-window burn-rate alerts are verified with a controlled failure.
- A restore drill proves an RPO/RTO measurement from a real backup.
- A saga failure is traceable across HTTP, broker and database boundaries by
  correlation/message identifiers.
- Readiness does not remove a pod for an optional dependency the service is
  designed to degrade around.

**Relative estimate:** medium, three to six sessions plus observation time.

**Expected result:** operators can detect, diagnose and recover critical flows
without reading source code during an incident.

## Phase 6 — Delivery and GitOps

**Objective:** promote one verified immutable artifact through environments with
declarative rollback.

**Tasks**

1. Separate artifact build from environment promotion. Publish only digest-
   addressed images that passed tests, scan, SBOM generation and signature.
2. Replace mutable `:latest` consumption with environment manifests referencing
   image digests; promote the same digest without rebuilding.
3. Add egress NetworkPolicies per workload and complete service identities/RBAC
   with least privilege.
4. Pin Helm chart versions in Ansible and record cluster-add-on compatibility.
5. Validate signatures at admission, render/validate every overlay in CI and add
   a staging smoke test before production approval.
6. Keep application and environment changes auditable through pull requests;
   use Argo CD reconciliation, drift detection and Git revert for rollback.
7. Add PDBs only for workloads where replica count and disruption behavior make
   the guarantee real; document singleton exceptions.

**Affected areas:** `.github/workflows/ci.yml`, `kubernetes/`,
`kubernetes/argocd/`, `iac/ansible/`, registry policy and deployment scripts.

**Dependencies:** Phases 1, 4 and 5; a production environment requires explicit
credentials, ownership and approval policy outside this repository.

**Risks:** deploying a different artifact than the scanned one, GitOps/HPA field
ownership conflicts, blocked egress and rollback across incompatible database
migrations.

**Acceptance criteria**

- Commit to pull request to signed digest to staging to production is traceable.
- The same digest is promoted; no environment rebuild occurs.
- Kustomize and schema validation pass for every environment overlay.
- Admission rejects unsigned/untrusted application images.
- Expand/contract migrations permit application rollback.
- A failed staging verification blocks promotion; production rollback is a
  reviewed Git change with a tested runbook.

**Relative estimate:** large, five to eight sessions.

**Expected result:** delivery is reproducible, policy-controlled and reversible.

## Executable backlog order

| Order | Item | Priority | Smallest useful increment | Proof |
| ---: | --- | --- | --- | --- |
| 1 | Rebaseline all audit findings | P0 | One status table with links to current evidence | Review against HEAD |
| 2 | Prove warehouse-safe replenishment | P0/P1 | Persist and consume originating warehouse identity | Unit + concurrent integration test |
| 3 | Complete Payments/Inventory resilience | P1 | One idempotent transaction path at a time | Fault-injection integration test |
| 4 | Pin runtime images | P1 | One shared runtime version/digest policy across seven Dockerfiles | Build + Trivy |
| 5 | Promotion calendar and budget | P1 | Time window, then exclusivity/priority, then atomic budget | Domain + PostgreSQL concurrency tests |
| 6 | Unify order transitions | P1 | Migrate one transition path per change | Architecture + integration tests |
| 7 | Close consumer reliability matrix | P1 | One consumer producer pair per change | Duplicate/order/restart tests |
| 8 | Add egress policy incrementally | P1 | One workload with explicit DNS and dependency egress | Kubeconform + lab smoke test |
| 9 | Alert existing terminal failures | P1/P2 | One alert and runbook per metric | Prometheus rule test + controlled failure |
| 10 | Automate restore and chaos evidence | P1/P2 | One non-destructive scheduled drill | Retained report + alert evidence |
| 11 | Harden remaining quality gates | P2 | Calibrate Hadolint, then ShellCheck, then npm audit policy | Zero unowned suppressions |
| 12 | Promote immutable digests | P1/P2 | Staging first, production after rollback proof | Signed digest and Git promotion trail |

## CI quality-gate map

| Gate | Pull request | Main | Scheduled/manual | Current mechanism |
| --- | ---: | ---: | ---: | --- |
| .NET format/analyzers/build | Blocking | Blocking | — | `ci.yml` + `Directory.Build.props` |
| Unit/architecture tests | Blocking | Blocking | — | Fast `build and test` job |
| Integration/contract tests | Blocking | Blocking | — | Separate Testcontainers job |
| Frontend lint/type-check/build/tests | Blocking | Blocking | — | Frontend job |
| Coverage | Blocking | Blocking | — | Unit-only calibrated threshold |
| Workflow, Bash and Dockerfile quality | Blocking | Blocking | — | `actionlint`, `bash -n`, ShellCheck, Hadolint |
| Kustomize/schema/admission policy | Blocking | Blocking | — | all overlays + Kubeconform + Kyverno CLI tests |
| Observability configuration | Blocking | Blocking | — | Promtool rules/config + Grafana JSON parsing |
| Secrets/dependencies/SAST | Blocking | Blocking | Weekly CodeQL | Gitleaks, NuGet/npm audit, CodeQL, Dependabot |
| Image CVEs/SBOM/signature | — | Blocks promotion | Rebuild/rescan to add | Trivy, Syft action, Cosign |
| Immutable environment promotion | — | Manual protected gate | Staging smoke before production | Digest PR + retained smoke evidence |
| Mutation testing | — | — | Weekly/manual | Stryker.NET |
| Concurrency/chaos/restore | Selected proof | Selected proof | Lab schedule/manual | Testcontainers and live-proof scripts |

## Definition of done for every backlog item

- The business and failure scenarios are stated before implementation.
- The change has the smallest appropriate unit, architecture, contract or
  integration tests and propagates cancellation for I/O.
- Logs are structured and contain no credentials or personal data.
- Metrics/alerts are added only when an operator action exists.
- Configuration is validated at startup and remains consistent across Compose
  and Kubernetes.
- Documentation names any lab-only validation that was not run locally.
- CI is green, the diff is focused, and a rollback or compatibility strategy is
  documented for contracts, schemas and deployment changes.

## Recommended execution cadence

Use one pull request per independently provable increment. P0/P1 work may move
through consecutive pull requests, but do not combine a domain behavior change,
schema migration, infrastructure policy and broad refactoring unless their
atomicity is necessary for compatibility. At the end of each phase, update this
roadmap with evidence links and re-prioritize the remaining risks instead of
advancing mechanically.
