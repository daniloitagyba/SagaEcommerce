# Milestone 16 SLOs + Multi-Window, Multi-Burn-Rate Alerting

## Scope

Every prior milestone's acceptance criteria have been threshold checks against a single k6 run: p95 latency, a fixed failure rate, a specific replica count. None of them answer the question an SLO answers: *is orders-api healthy enough, over time, for the error budget it's actually allowed?* This milestone defines that budget and implements the alerting strategy the Google SRE workbook recommends for it - multi-window, multi-burn-rate alerts - rather than a single noisy threshold.

## Design

- **The SLO: 99.5% of `orders-api` requests succeed (non-5xx) over a rolling 30 days** - a 0.5% error budget. This is a target, not a measurement of this specific lab's actual historical uptime (which has been effectively 100% outside deliberate chaos experiments); it exists to give the burn-rate math above something concrete to be a fraction of.
- **Burn rate = observed error ratio ÷ (1 − SLO target).** A burn rate of 1 means "spending the 30-day budget exactly on schedule"; 14.4 means "exhausting it in about two hours if sustained." The recommended two-window pairs from the SRE workbook: a **page**-worthy rule requires the error ratio to exceed a 14.4× burn on *both* a 5-minute and a 1-hour window simultaneously; a **ticket**-worthy rule requires a 6× burn on *both* a 30-minute and a 6-hour window. Requiring both windows is the entire point: a single brief spike burns the short window instantly but barely moves the long one, so it can't page on its own - a real sustained problem shows up in both.
- **Recording rules** (`observability/prometheus/rules/orders-api-slo.yml`) precompute the request rate, failed-request rate, and their ratio at six window sizes (30s, 2m, 5m, 30m, 1h, 6h) from the same `http_server_request_duration_seconds_count` metric the Milestone 15 canary analysis already uses. Both sides of every ratio fall back to a zero-result vector (`or vector(0)` / `or vector(1)`) rather than "no data" - the same empty-vector-division bug fixed in the canary `AnalysisTemplate` last milestone would otherwise resurface here too.
- **A third, short-window "demo" alert rule** runs the identical 14.4×-burn math as the real page rule, just on 30-second/2-minute windows instead of 5-minute/1-hour. This exists purely so the alerting pipeline - recording rules feeding a burn rate, the burn rate crossing a threshold, `ALERTS` entering `firing`, Alertmanager receiving it, and the state clearing on recovery - can be validated within a few minutes of induced failure rather than requiring an actual sustained hour of errors. The page and ticket rules ship with the textbook windows as the real, intended configuration; the demo rule is clearly labeled as a validation aid, not something meant to page anyone.
- **Alertmanager**, installed via Compose, gives Prometheus a real routing target with grouping, inhibition (a firing `page` suppresses a matching `ticket` for the same alert), and resolution - the full pipeline a `groups: [...] -> ALERTS` metric alone doesn't exercise. It has a `null` receiver: this is a personal lab, not an on-call rotation, so no real notification integration (Slack, email, PagerDuty) is wired up. What's validated is that alerts actually flow through and resolve correctly, not where a notification would eventually land.

## What didn't work

**Generating a real, sustained 5xx rate without also tripping the readiness probe is harder than it sounds.** The obvious way to inject failures - break Postgres via Toxiproxy, the same mechanism Milestone 10's chaos tests already use - doesn't work for this specific purpose: `/health/ready` checks Postgres too, so a broken database fails the *readiness probe* within about 15 seconds, and Kubernetes pulls the pod from the Service entirely. After that, requests fail to connect rather than completing with a clean 5xx status, so the metric this milestone's alert actually watches (`http_response_status_code=~"5.."` on *completed* HTTP requests) never sees the failure at all - the pod is just gone from load balancing. The fix was to reuse Milestone 15's approach instead: redeploy the same deliberately-broken build from the canary rollback demo (every `POST /orders` throws; health checks are untouched, since they don't call that code path), so pods stay `Ready` and continue actually receiving and failing real requests.

**A burn-rate alert clears the instant traffic stops, independent of whether the underlying defect is fixed.** The first validation run deployed the broken build, generated about 2.5 minutes of load, and confirmed `OrdersApiErrorBudgetBurnDemo` reached `firing` and that Alertmanager received it - both true. But by the time this was checked a short while later (with no code fix yet applied), the alert had already cleared, because the k6 load run itself had finished and no further requests were arriving: a `rate()`-based ratio over an idle window reads as "no burn," not "last known bad state." This is correct behavior, not a bug - a burn-rate SLI can only measure burn where there's traffic to burn against - but it means "the alert cleared" only proves "traffic stopped," not "the fix worked." Confirming the fix specifically required reverting to the good build *first*, then generating a fresh load run against it and confirming the alert stayed inactive throughout - which is the sequence actually recorded below.

**Deploying the deliberately-broken build a second time got auto-promoted to 100% by Milestone 15's own canary analysis** before this milestone's load test had ramped up enough traffic to reach the canary weight (33%) during its brief analysis window - the two milestones' fault-injection timelines didn't line up, so the "canary catches it" story from Milestone 15 didn't repeat here. This actually made this milestone's own test *easier*, not harder: with the bad build at 100%, the subsequent k6 load generated failures across every pod instead of just a third of them, comfortably clearing the burn-rate threshold once real traffic did arrive.

## Results

### Rule evaluation

All 18 recording rules and 3 alert rules loaded with `health: ok` (recording) on first Prometheus reload; Prometheus's `/api/v1/alertmanagers` confirmed it discovered `alertmanager:9093` immediately.

### Validation: broken build, real traffic, alert fires

| Step | Observed |
| --- | --- |
| Baseline | `OrdersApiErrorBudgetBurnDemo` = `inactive` |
| Deploy known-broken build (via git + Argo CD, per Milestone 15) | Canary reached 100% without the Milestone 15 analysis catching it (traffic-timing mismatch, see above) |
| ~2.5 minutes of k6 `baseline` load against the now-100%-broken deployment | `ALERTS{alertname="OrdersApiErrorBudgetBurnDemo"}` → `alertstate: firing`, `severity: page` |
| Alertmanager check | `alertmanager:9093/api/v2/alerts` confirmed receipt during the firing window |
| Load stops | Alert clears - traffic-dependent, as discussed above, not evidence the build was fixed |

### Validation: revert, real traffic, no false alarm

| Step | Observed |
| --- | --- |
| Revert to the known-good build (via git + Argo CD) | Rollout promoted cleanly through all three canary steps to `Healthy` |
| k6 `baseline` load against the reverted build | `failed_rate=0`, `checks_rate=100%`; `OrdersApiErrorBudgetBurnDemo` query returned zero results (never entered `pending` or `firing`) throughout |

### Regression check

`dotnet test` (24 + 7 passing), `smoke`, and `saga` (100% converged, 100% correct outcome on this run) all pass cleanly against the final, reverted state.

## Running the experiment

```bash
kubectl get application saga-ecommerce -n argocd  # confirm GitOps state before starting
scripts/slo-burn-rate-test.sh generate-load     # after deploying a known-broken build
scripts/slo-burn-rate-test.sh confirm-resolved  # after reverting it
```
