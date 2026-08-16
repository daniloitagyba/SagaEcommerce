# Alert Provocation Runbook

`observability/prometheus/rules/correctness-invariants.yml`'s own header says its thresholds are "not yet calibrated... each should be proven to actually fire using the drill/chaos script noted per alert before being trusted as a real page." That note names a script per alert but nothing tracked whether the provocation actually happened. This table is that tracking - one row per `severity: page` correctness-invariant alert (the ones that wake someone up, not just file a ticket), with the script that should provoke it and the last time someone ran it and confirmed the alert fired.

**This table is a log, not a claim.** A blank "last confirmed fired" cell means exactly that - nobody has proven the alert fires yet, not that it's known to work. Update the cell only after actually running the provocation against a live deployment and watching the alert transition to `firing` in Alertmanager.

## severity: page

| Alert | Provocation script | Last confirmed fired | Notes |
|---|---|---|---|
| `AntiEntropyDivergenceDetected` | `scripts/live-proofs/inventory-reservation-concurrency-test.sh` or `scripts/chaos/resilience-chaos.sh` | _(not yet run)_ | Check `AntiEntropySweeper`'s own logs for which check fired, per the rule's own comment. |
| `SettlementReconciliationUnresolved` | _(no dedicated script exists yet)_ | _(not yet run)_ | Closest available lever: force a settlement mismatch by cancelling an order's payment authorization out from under an in-flight capture (see `PaymentSettlementProcessorTests.ACaptureThatArrivesAfterTheHoldAlreadyExpiredPublishesAMismatchReplyInsteadOfSilence` for the shape of the race) and confirm the reply's `RequiresReconciliation: true` flag actually produces the metric increment in a live deployment, not just the unit test. A real provocation script under `scripts/live-proofs/` would close this gap properly. |
| `RateLimitingFailedOpen` | `scripts/chaos/resilience-chaos.sh` (Redis outage window) | _(not yet run)_ | Confirm `orders_rate_limit_distributed_bypassed_total` increments and the alert fires within the Redis-down window the script creates, not just that requests keep succeeding. |

## Why this list is short

`OrphanedSagaRepliesHigh`, `OrderCacheFailingOpen`, `FencedWriteRejectedSustained`, and `ProjectionLagHigh` are all `severity: ticket`, not `severity: page` - a missed provocation on one of those means a delayed ticket, not a missed page. They're worth the same eventual treatment, but the P0/P1 gap this runbook closes first is specifically "does the thing that's supposed to wake someone up actually wake someone up."

## How to use this table

1. Pick a row with a blank "last confirmed fired" cell, or one whose date is stale enough to no longer trust (a threshold, a metric name, or the underlying code path can all drift since the last confirmation).
2. Run the provocation script against a live deployment (the K3s lab, per `docs/saga/milestone-75-saga-mode-both-by-default.md`'s "Full solution, real Testcontainers, on the lab server" precedent - this class of validation does not run from a local sandbox, see `ENVIRONMENT.md`).
3. Watch Alertmanager (or Prometheus's own Alerts page) for the rule to transition to `firing`, not just for the underlying metric to move - a metric incrementing and an alert actually paging are different claims, and this table only tracks the second one.
4. Update the row's date and, if anything about the provocation or the alert's behavior was surprising, add a note.
