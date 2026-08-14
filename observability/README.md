# Observability

OpenTelemetry (traces/metrics/logs) from every service, collected by
`otel-collector/`, stored in Prometheus/Tempo/Loki, visualized in Grafana
(`grafana/dashboards/`, auto-provisioned - drop a `.json` file in that
directory and it appears under the "Distributed Systems Lab" folder within
`updateIntervalSeconds` of `grafana/provisioning/dashboards/dashboards.yaml`,
no restart needed in practice, though one guarantees it picks it up).

## Dashboard scope, and why it isn't uniform

| Dashboard | Panels | Why |
|---|---|---|
| `orders-overview.json` (Orders Lab Overview) | Orders created/processed, outbox, inbox, cache, coupon/pricing, saga, plus Milestone 79's dead-letter/outbox-backlog/consumer-lag panels | Orders.Api/Worker + Payments.Service are where almost all of this repo's custom application metrics live (`BuildingBlocks.Observability/OrdersTelemetry.cs`) - outbox, inbox, idempotency, fencing, rate limiting, projection lag. |
| `inventory-overview.json` | Generic RED (request rate/error rate/latency) + GC heap, **plus** dead letters, outbox backlog, and Kafka consumer group lag scoped to `service_name="inventory-service"` | Inventory.Service is the only one of the other four that uses the shared `OutboxPublisher<TDbContext>`/`KafkaConsumerHost<T>` building blocks, so it actually emits `messaging.dead_letters`, `outbox.messages.pending`, and has real consumer groups worth watching - not narrated, checked (`grep -rn CreateCounter apps/src/Inventory.Service` before writing this file). |
| `cart-overview.json`, `catalog-overview.json`, `storefront-overview.json` | Generic RED (request rate/error rate/p50-p95-p99 latency) + GC heap only | These three emit **no custom application metrics at all** today - Cart.Service (Redis CRDT state, no outbox/Kafka), Catalog.Service (MongoDB reads, no outbox/Kafka), and Storefront.Service (pure BFF proxy, no persistence or messaging of its own) only produce the generic ASP.NET Core/.NET runtime instrumentation every service gets from `BuildingBlocks.Observability`. A dashboard with more panels than that would be decorative, not informative - if one of these services grows its own domain metrics later, this is the file to extend. |

All five dashboards use the same `http_server_request_duration_seconds*`
(ASP.NET Core auto-instrumentation) and `dotnet_gc_last_collection_heap_size_bytes`
(.NET runtime instrumentation) metric families, scoped per service via the
`service_name` label every service's OTel resource attributes carry
(`ObservabilityExtensions.cs`) - the same label
`scripts/live-proofs/memory-leak-check.sh` and `observability/prometheus/rules/orders-api-slo.yml`
already filter on.

## Verifying a dashboard actually shows data

Query Prometheus directly for the metric a panel uses before trusting the
panel, e.g.:

```bash
curl -s 'http://localhost:9090/api/v1/query?query=http_server_request_duration_seconds_count{service_name="cart-service"}'
```

An empty result for a counter that legitimately hasn't fired yet (dead
letters, retries - nothing has failed) is correct behavior, not a broken
panel; an empty result for something that should always have data (request
rate, GC heap) means the `service_name` value or metric name is wrong.
