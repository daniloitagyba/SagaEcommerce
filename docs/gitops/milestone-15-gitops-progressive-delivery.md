# Milestone 15 GitOps + Progressive Delivery

## Scope

Every previous milestone was deployed the same way: edit locally, `rsync` to the server, then `scripts/k3s-deploy.sh` runs `kubectl apply` directly against the live cluster. This milestone replaces that for `orders-api` with [Argo CD](https://argo-cd.readthedocs.io) reconciling `kubernetes/overlays/local` from this repo's `main` branch, and adds [Argo Rollouts](https://argoproj.github.io/argo-rollouts/) so `orders-api` deploys as a canary with an automated, Prometheus-backed analysis gate instead of an all-at-once rolling update.

## Design

- **Argo CD, installed via Helm**, watches `kubernetes/overlays/local` in this (private) GitHub repo directly — no export step, no separate deploy manifest to keep in sync. A dedicated, repo-scoped, **read-only** SSH deploy key was generated on the server and registered on GitHub for this (`gh repo deploy-key add`), rather than reusing any broader credential.
- **Migration Jobs became Argo CD PreSync hooks** (`argocd.argoproj.io/hook: PreSync`, `hook-delete-policy: BeforeHookCreation`) instead of plain Jobs. This is the GitOps-native version of the exact "Kubernetes Jobs are immutable once Completed" problem fixed imperatively in `k3s-deploy.sh` back in Milestones 12–13 (delete-then-apply before every deploy): Argo CD now deletes and reruns the migration Job automatically before every sync, with no script involved.
- **`orders-api` becomes an Argo Rollout** instead of a Deployment, with a basic (replica-weighted, no service mesh) canary strategy: 33% weight → 30s pause → automated analysis → 66% weight → 20s pause → 100%. The HPA from Milestone 8/13 now targets the Rollout (`scaleTargetRef.kind: Rollout`) instead of the Deployment — HPA-driven replica count and Rollout-driven canary weighting compose natively; Argo Rollouts splits whatever total replica count the HPA decides on between the stable and canary ReplicaSets according to the current step's weight.
- **The analysis gate is a Prometheus query, not a human.** `AnalysisTemplate orders-api-error-rate` asks Prometheus for the 5xx ratio over the last minute, sampled three times at 15-second intervals, and fails if any single reading is ≥ 5%. `orders-worker` and `payments-service` are left as plain Deployments — only `orders-api` (the component this repo's whole load-testing suite already targets) gets the canary treatment, keeping the blast radius of this milestone's design to what it can actually validate.
- **A deliberately broken build was used to prove the rollback actually works**, not just read about it. `POST /orders` was made to unconditionally throw (health checks are untouched, since they don't call that code path — the point is a canary that's `Ready` and receiving real traffic, not one caught by a stalled rollout before it ever serves anything), built under a distinct image tag, committed, and pushed. Argo CD picked it up, the Rollout proceeded to 33% weight, and the AnalysisRun caught it.

## What didn't work

**Argo CD's `selfHeal` fights any change you make with `kubectl` instead of git.** Immediately after converting `orders-api` from a Deployment to a Rollout, a manual `kubectl apply --kustomize` (out of old habit) created the new Rollout and deleted the old Deployment directly against the cluster — before committing the change to git. `selfHeal: true` means Argo CD continuously reconciles toward whatever git *currently* says, and git still said "Deployment" at that moment: it recreated the Deployment it thought had gone missing, fighting the very migration in progress. The fix isn't a workaround, it's the actual discipline GitOps is supposed to enforce: commit and push first, then let Argo CD apply it (optionally forcing an immediate reconcile with `kubectl annotate application ... argocd.argoproj.io/refresh=hard`, rather than waiting out its poll interval) — never `kubectl apply` a resource Argo CD already owns.

**KEDA's exact cross-namespace Kafka problem (Milestone 14) recurs for any cluster-internal tool that isn't in `orders-lab`.** Argo Rollouts' AnalysisRun controller needed to reach Prometheus, which — like Kafka before it — was only reachable from inside `orders-lab` (bound to `127.0.0.1` on the host besides that). Same fix shape as Milestone 14: gave Prometheus a static IP on the `k3s-bridge` network plus a matching Service/EndpointSlice, so `http://prometheus.orders-lab.svc.cluster.local:9090` resolves and connects from any namespace, including `argo-rollouts`.

**A ratio query against a metric with zero matching samples doesn't return zero — it returns nothing, and "nothing" crashes result parsing instead of failing cleanly.** The first real analysis run against the deliberately-broken build errored with `reflect: slice index out of range` rather than reporting a clean 100% error rate. The cause: `sum(rate(...{http_response_status_code=~"5.."}[1m]))` returns an *empty* vector — not a zero-value one — whenever no 5xx samples exist yet in the window (e.g., in the few seconds before the canary receives its first request), and dividing an empty vector by anything is still empty. Argo Rollouts' analysis provider tries to read `result[0]` off that empty result and panics. The fix is a standard PromQL idiom: wrap both sides of the division in `... or vector(0)` / `... or vector(1)` so the query always returns exactly one data point, ratio well-defined, even when nothing has happened yet. (The *first* deliberately-broken deploy still triggered an automatic rollback despite this bug — Argo Rollouts correctly treats a repeatedly-erroring analysis provider as unsafe and aborts rather than proceeding blind. That's a legitimate safety property in its own right, just not the "measured a real elevated error rate" story this milestone specifically set out to demonstrate — which the second attempt, after the query fix, did cleanly.)

**Converting `orders-api`'s kind broke every script that assumed "Deployment."** `kubectl rollout status`/`restart deployment/orders-api`, and reads of the Deployment-specific `deployment.kubernetes.io/revision` annotation, appear in `k6-run.sh`, `k3s-deploy.sh`, `k3s-smoke-test.sh`, `hpa-test.sh`, `resilience-test.sh`, and `resilience-chaos.sh` — none of it applies to a `Rollout`, which has no `kubectl rollout` support at all and tracks revisions via its own `rollout.argoproj.io/revision` annotation. Fixed each to poll the Rollout's own status fields directly and use its `spec.restartAt` field in place of `kubectl rollout restart`. Also replaced `hpa-test.sh`'s hardcoded "2 replicas" (stale since Milestone 13 raised the HPA floor to 3, independent of this milestone) with the HPA's `minReplicas` read dynamically, so it stops silently drifting out of date the next time the floor changes.

## Results

### GitOps reconciliation

| Check | Result |
| --- | --- |
| First sync, against the already-deployed Milestone 14 state | `Synced` / `Healthy` immediately — no drift |
| Push a real commit (migration Jobs → PreSync hooks) | Auto-detected; `status.sync.revision` matched the new commit; both migration Jobs visibly rebuilt (fresh pod names, `age` reset to seconds) |
| Push the Rollout conversion | Same — new commit picked up, canary strategy applied |

### Progressive delivery: bad build

| Step | Weight | Rollout phase | AnalysisRun |
| --- | ---: | --- | --- |
| Canary created | 33% | `Paused` (step pause) | — |
| Analysis step reached | 33% | `Progressing` → `Degraded` | `Error` (first attempt: query bug, see above) |
| Traffic during canary | 33% | — | k6 `baseline`: `failed_rate` far above threshold on every create — the deliberate defect, confirmed live |
| Outcome | — | Rollout **aborted**, reverted to `stableRS` | Automatic — no manual rollback command was run |

### Progressive delivery: good build (after reverting the defect, query fixed)

| Step | Weight | Rollout phase | AnalysisRun |
| --- | ---: | --- | --- |
| Canary created | 33% | `Paused` → `Progressing` | Created, `Running` |
| Analysis completes | 33% | `Progressing` | `Successful` — 3/3 measurements, `error-rate = 0` each time |
| Promotion | 66% → 100% | `Paused` → `Progressing` → `Healthy` | — |
| Load during rollout | — | — | k6 `baseline`: `failed_rate=0`, `checks_rate=100%`, `flow_rate=100%` |

Full cycle (canary created → fully promoted `Healthy`) took under two minutes, entirely automatic — the only human action was `git push`.

### Regression check

`smoke`, `saga`, and `baseline` all pass cleanly post-migration. `saga` shows a consistent ~99.7% `saga_correct_outcome_rate` against its `rate==1` threshold across repeated runs (346–383 iterations, always exactly one rare timeout past the 20-second convergence-poll window) — present before this milestone's changes and unrelated to them (neither `payments-service` nor the saga consumers were touched here); `failed_rate` and `checks_rate` are consistently clean. Documented rather than chased further, since it's outside this milestone's scope.

## Running the experiment

```bash
kubectl get application saga-ecommerce -n argocd
kubectl get rollout orders-api -n orders-lab
kubectl get analysisrun -n orders-lab
kubectl annotate application saga-ecommerce -n argocd argocd.argoproj.io/refresh=hard --overwrite  # force an immediate sync instead of waiting on the poll interval
```
