# Milestone 79: Alerting Beyond the One Golden Signal Orders.Api Had

## Scope

Milestone 16 built real multi-window, multi-burn-rate alerting - but only for one thing: `orders-api`'s HTTP error rate. Every asynchronous path in this system had none. A Kafka consumer stuck on a poison message, an outbox publisher that stopped running, or a message that landed in a dead-letter topic were all silent - visible only if someone happened to open `kafka-ui` or grep a container's logs by hand. This milestone adds three operational alerts: dead letters, outbox backlog, and Kafka consumer-group lag.

## What existed already vs. what was missing

Two of the three signals already had a real, wired metric with nobody watching it:

- **Dead letters**: `messaging.dead_letters` (`Counter<long>`) has existed since Milestone 62 (`KafkaConsumerHost.cs`), incremented every time a message exhausts its retries and gets dead-lettered. Nothing ever alerted on it.
- **Outbox backlog**: `outbox.messages.published`/`outbox.publish.retries` counters existed, but neither tells you the *current* backlog - a rate of successful publishes looks identical whether the queue is empty or growing, if the growth rate matches the publish rate. There was no gauge for "how many rows are waiting right now."
- **Consumer lag**: nothing at all. No exporter in the stack had ever scraped Kafka consumer-group offsets against log-end offsets.

## Changes

**A real outbox backlog gauge, not an inferred one.** `OutboxPublisher<TDbContext>.ProcessBatchAsync` (`BuildingBlocks.Persistence`) already runs a `SELECT ... WHERE processed_at IS NULL FOR UPDATE SKIP LOCKED LIMIT @batch_size` every poll cycle (500ms by default). It now also runs `COUNT(*) WHERE processed_at IS NULL` - the same predicate, so it uses the same partial index (`ix_outbox_messages_pending`) the polling query already relies on - and records it via a new `Gauge<long>` (`OrdersTelemetry.RecordOutboxPending`, metric `outbox.messages.pending`). Since `OutboxPublisher<TDbContext>` is the one shared class every outbox-backed service (Orders.Api/Worker, Payments.Service, Inventory.Service) already uses, this one change instruments all of them.

**`kafka-exporter` added to the stack.** `danielqsj/kafka-exporter:v1.9.0`, pointed at `kafka:9092`, exposing `kafka_consumergroup_lag` per consumer-group/topic/partition. New Prometheus scrape job (`kafka-exporter:9308`). Compose-only, matching where Milestone 16's own alerting already lived - this repo has no Kubernetes-side Prometheus deployment to extend.

**Three new alert rules** (`observability/prometheus/rules/messaging-ops.yml`):

| Alert | Condition | Reasoning |
|---|---|---|
| `DeadLetterMessagesDetected` | `increase(messaging_dead_letters_total[5m]) > 0`, `for: 0s` | Any dead letter is actionable - Milestone 62 built redrive specifically because a DLQ message doesn't resolve itself. Fires on the first one. |
| `OutboxBacklogGrowing` | `max(outbox_messages_pending) > 25`, `for: 5m` | The publisher polls every 500ms; a backlog that stays above a small cushion for 5 minutes means publishing has stalled, not that it's momentarily behind a burst. |
| `KafkaConsumerGroupLagHigh` | `max by (consumergroup, topic) (kafka_consumergroup_lag) > 200`, `for: 5m` | Every consumer group here processes in well under a second per message (Milestone 51's own measurement); lag in the hundreds sustained for 5 minutes means a consumer stopped keeping up, not normal jitter. |

**Three new Grafana panels** on the existing `Orders Lab Overview` dashboard: "Outbox backlog (pending messages)" and "Dead letters (last 15m)" as threshold-colored stat panels (green until the same 25/1 the alerts use, red above), and "Kafka consumer group lag" as a timeseries broken out by consumer group and topic.

## Threshold calibration, against real measurement

All three thresholds were checked against this lab's actual steady-state traffic, not guessed:

```
kafka_consumergroup_lag        0   across every consumer group/topic, at rest
outbox_messages_pending        0   at rest, briefly nonzero (never above single digits) under real order traffic
messaging_dead_letters_total   no series - never incremented since these services last started
```

25 and 200 both sit comfortably above a noise floor that, in practice, never left zero even while placing real orders through the live API. That's a wide margin deliberately - a lab this size has no baseline for what "normal but busy" load looks like yet, so the thresholds are set to catch an actual stall (publisher down, consumer wedged) rather than to be tight around a load profile that hasn't been observed under real production-scale traffic.

## A stale-bind-mount trap, found while validating this live

`prometheus.yml` is bind-mounted into the Prometheus container as a single file. Docker resolves a single-file bind mount to a specific inode at container start; `rsync`'s default replace-via-rename means the file on disk gets a *new* inode, and the running container keeps reading the old one - `curl -X POST /-/reload` returns 200 and reports success, but the config it reloads is whatever the stale bind mount still points at. This silently ate the new `kafka-exporter` scrape job on the first deploy attempt (the rules directory, mounted as a whole directory rather than a single file, doesn't have this problem - that's why the new alert rules loaded fine on the first try while the scrape config didn't). Fixed by `docker compose up -d --force-recreate prometheus`, which drops and remakes the bind mount against the current file. Worth remembering for any future single-file-mounted config in this stack, not just this one.

## Live validation (real Compose stack)

- `kafka_consumergroup_lag`, `outbox_messages_pending`, and `messaging_dead_letters_total` all confirmed present in Prometheus with real, sensible values after redeploying `orders-api`, `orders-worker`, `payments-service`, and `inventory-service`.
- Five real orders placed through `POST /orders` to generate genuine outbox/consumer traffic - `rate(outbox_messages_published_total[1m])` moved off zero, confirming the whole pipeline (app metric -> OTel collector -> Prometheus) carries real signal, not just the gauge's idle value.
- All three rules evaluate with `health: ok`, `lastError: null`, currently `inactive` - correct, since nothing is actually wrong right now.
- Grafana's dashboard API confirms all three new panels provisioned (25 panels total, up from 22).
- No new errors in any of the five redeployed services' logs.

## What this doesn't cover

No real notification integration exists (Milestone 16's own scope note still applies - `alertmanager.yml`'s receiver is `null-receiver`, a personal lab, not an on-call rotation). These alerts prove Alertmanager's routing/grouping/inhibition pipeline works end-to-end for three new signals, the same thing Milestone 16 proved for one. Consumer-lag alerting is per consumer-group/topic/partition as `kafka-exporter` reports it - it doesn't attempt to correlate a lagging consumer back to a specific saga step or order, only surfaces that one exists.
