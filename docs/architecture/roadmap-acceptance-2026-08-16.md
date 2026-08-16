# Roadmap Acceptance Record — 2026-08-16

## Decision

All implementation and acceptance work in
[`implementation-roadmap.md`](implementation-roadmap.md) is complete for the
repository and the available lab/staging environment. No known roadmap item is
left pending.

Production acceptance is deliberately scoped to the repository's actual
capability: there is no production Kubernetes destination or production Argo CD
Application in this repository. The production proof therefore exercised the
complete signed-digest, protected-PR promotion and Git rollback transaction; it
does not claim that customer traffic was changed in a nonexistent cluster.

The adjacent
[`roadmap-acceptance-2026-08-16.json`](roadmap-acceptance-2026-08-16.json)
contains the same evidence in machine-readable form.

## Accepted evidence

| Capability | Evidence | Accepted result |
| --- | --- | --- |
| Build, tests, images and supply chain | CI [31971995135](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31971995135), CodeQL [31971995182](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31971995182) | Revision `9a1f8d7200fae8826186c08a29242372dc884c8b` passed blocking build/unit/architecture/integration/contract/frontend/security gates. Seven images were built, scanned, given SBOMs and signed. |
| Real backup/restore | Workflow [31973821277](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31973821277) | MongoDB: 9 products and 4 categories, backup 1 s, restore 3 s, measured RPO 0 records. Redis: 45 keys, backup 0 s, restore 2 s, measured RPO 0 keys. Evidence retention is the repository maximum of 90 days. |
| Immutable staging | Promotion PR [#21](https://github.com/daniloitagyba/SagaEcommerce/pull/21), smoke [31976283730](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31976283730) | All seven running image IDs matched the signed immutable digests. Six real SKU/payment orders reached `Confirmed` or `Cancelled`; worker consumption, Loki indexing and readiness passed. |
| Mesh and egress | Argo CD revision `294b2522444013d31a00993d0a7ad2b73f68341a` | `Synced/Healthy`; Linkerd identities and authorization remained enforced. Orders-to-Catalog traffic succeeded through the Service port after the smoke exposed the original wrong-port configuration. |
| Chaos recovery | Chaos Mesh [31976574923](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31976574923) | The worker was killed after order 10 of 20. It became ready in 14 s; 20/20 orders converged in 111 s; measured data loss was 0. |
| Page alerts | Lab artifacts under `artifacts/alerts/` and the [provocation runbook](../resilience/alert-provocation-runbook.md) | `SettlementReconciliationUnresolved`, `RateLimitingFailedOpen` and scoped `AntiEntropyDivergenceDetected` each reached `firing`; all injected state/dependency faults were cleaned up. |
| Multi-window burn rate | Lab artifact `artifacts/k6/20260816T224220Z-slo-burn-rate/` | Controlled PostgreSQL outage generated 1,168 requests and 768 5xx responses. `OrdersApiErrorBudgetBurnDemo` fired, PostgreSQL/readiness recovered, and the alert resolved. |
| Production promotion | Workflow [31977159873](https://github.com/daniloitagyba/SagaEcommerce/actions/runs/31977159873), PR [#22](https://github.com/daniloitagyba/SagaEcommerce/pull/22) | Matching successful staging evidence was required; all seven signatures/digests were reverified; the protected promotion PR passed all gates and merged. |
| Production rollback | PR [#23](https://github.com/daniloitagyba/SagaEcommerce/pull/23) | The promotion merge was reverted as an auditable Git change, passed the same blocking gates, and merged. No artifact was rebuilt or retagged. |

## Superseded evidence

Acceptance excludes evidence that was green without proving the required
behavior:

- staging smoke `31975041536` created legacy amount-only orders and only
  observed a worker log;
- chaos run `31975086678` reported 0 converged/20 lost but the workflow stayed
  green because the outer pipeline did not propagate the script exit code;
- staging smoke `31975970589` correctly failed after fail-closed hardening and
  exposed Orders-to-Catalog calls using container port 8080 instead of the
  Kubernetes Service port 80;
- the first anti-entropy attempt could match an unrelated firing series; the
  final proof is scoped to `check=backorder_on_dead_order` and accounts for the
  first Prometheus sample establishing a counter baseline.

These findings produced fixes rather than waivers: workflow pipelines use
`pipefail`, smoke/chaos create real item orders and validate terminal states,
alert proofs are label-scoped, and the Kubernetes catalog URL uses the Service
port.

## Cleanup and final state

- Obsolete invalid acceptance records created by the legacy amount-only tests
  were reconciled in the lab only; no production data was changed.
- Synthetic alert markers, PodChaos resources and outage state were removed.
- PostgreSQL was healthy, Argo CD was `Synced/Healthy`, and all application
  workload containers were ready after the final operational proof.
- The final repository state is intentionally the post-rollback state. PR #22
  remains the immutable production-promotion trace and PR #23 the rollback
  trace.
