# SagaEcommerce

[![ci](https://github.com/daniloitagyba/SagaEcommerce/actions/workflows/ci.yml/badge.svg)](https://github.com/daniloitagyba/SagaEcommerce/actions/workflows/ci.yml)

![Architecture overview](docs/images/architecture-dracula.png)

A distributed-systems lab built as a real e-commerce system, one milestone at a time. Seven .NET services (`Orders.Api`/`Orders.Worker`, `Payments.Service`, `Catalog.Service`, `Inventory.Service`, `Cart.Service`, `Storefront.Service`) coordinate through Kafka, backed by PostgreSQL, MongoDB, and Redis. Every milestone's report under [`docs/`](docs/) records what actually happened validating it against a live deployment — including what broke.

## What it demonstrates

- **Sagas** — choreographed and orchestrated, side by side, for comparison. Reserve inventory → decide payment → commit or *compensate*.
- **A real pricing domain** — line items priced server-side, promotions composed by a rules engine (NRules), coupons, loyalty tiers, multi-warehouse allocation with backorders, authorize-then-capture payments. See [`docs/domain/`](docs/domain/), starting at [Milestone 66](docs/domain/milestone-66-line-items-pricing-and-risk.md).
- **Delivery guarantees** — transactional Outbox + Inbox, at-least-once Kafka processing, a durable event log.
- **Polyglot persistence** — Postgres for transactions, MongoDB for the catalog, Redis as a real system of record for carts, not just a cache.
- **Resilience & chaos engineering** — Polly pipelines proven against real fault injection (Toxiproxy, Chaos Mesh), not just configured.
- **CQRS, event sourcing, CDC** — a denormalized read model, an append-only event store, Avro schema evolution, Debezium.
- **Formal & property-based verification** — a TLA+ model of the saga, CsCheck generating 10,000 orders per pricing invariant.
- **Quality gates in CI** — complexity/size limits, secrets and CVE scanning, mutation testing, coverage — each calibrated against a measurement, not a guess.
- **GitOps & progressive delivery** — Argo CD + Argo Rollouts canaries with automatic rollback on a Prometheus analysis template.
- **Service mesh & zero-trust** — Linkerd mTLS, Keycloak JWTs, Kyverno-enforced signed images.
- **Full observability** — traces, metrics, and logs correlated end to end via OpenTelemetry, in Grafana.

See [`docs/`](docs/) for the full, dated write-up of every milestone.

## Quickstart (Docker Compose)

Requires Docker with Compose v2. Everything runs on your machine — no external server or cluster needed.

```bash
cd compose
cp .env.example .env
# edit .env: replace the placeholder passwords with your own random values

docker compose up --detach --wait                          # infrastructure: Postgres, Kafka, Redis, MongoDB, Keycloak, observability stack
docker compose --profile compose-apps up --detach --wait    # all seven application services

../scripts/keycloak-configure-realm.sh                      # one-time: creates the auth realm/client the API expects
```

Then get a token and create an order:

```bash
TOKEN=$(../scripts/keycloak-get-token.sh)

# The server prices the line items against the live catalog and applies
# whatever promotions match - the request never states a price.
curl --request POST http://127.0.0.1:8088/orders \
  --header "Authorization: Bearer $TOKEN" \
  --header 'Content-Type: application/json' \
  --data '{
    "customerId": "customer-42",
    "items": [{"sku": "SKU-BOOK-001", "quantity": 2}, {"sku": "SKU-ELEC-001", "quantity": 1}],
    "couponCode": "SAVE10"
  }'
```

The response carries the full breakdown — subtotal, each promotion that fired, shipping, tax, and every line's share of the discount:

```jsonc
{
  "amount": 3816.73,
  "pricing": {
    "subtotal": 4479.70,
    "discountTotal": 662.97,   // SAVE10 (10%) + 5% off electronics, stacked
    "shippingTotal": 0,        // free above 200.00
    "lines": [ /* per-line unitPrice, lineDiscount, lineTotal */ ]
  }
}
```

Seeded coupons: `SAVE10` (unlimited), `SAVE20` (min. 200,00, 5 per customer), `HALFOFF` (50% off, min. 100,00, capped at 100 total, 1 per customer) — see [Milestone 67](docs/domain/milestone-67-coupon-lifecycle.md).

- Kafka UI: `http://127.0.0.1:8080`
- Grafana: `http://127.0.0.1:3000`
- Prometheus: `http://127.0.0.1:9090`

Bring everything down with `docker compose --profile compose-apps down`.

A few pieces of infrastructure are opt-in, behind their own Compose profile, since they don't participate in the flow above:

| Profile | Brings up | Used by |
|---|---|---|
| `postgres-ha` | MinIO (backup target) | `kubernetes/data-platform/postgres-ha-*.yaml`, `scripts/postgres-ha-provision.sh` |
| `cdc` | Debezium + its Kafka topics | [Milestone 21](docs/messaging/milestone-21-debezium-cdc.md) |
| `profiling` | Grafana Pyroscope | K3s only — [continuous profiling](docs/architecture/continuous-profiling.md) |
| `kafka-quorum-demo` | 3-broker Kafka quorum | `scripts/kafka-quorum-durability-test.sh` |
| `mongo-replicaset-demo` | 3-node MongoDB replica set | `scripts/mongo-replica-set-test.sh` |

e.g. `docker compose --profile cdc up --detach --wait`.

## Run the tests

```bash
cd apps
dotnet restore SagaEcommerce.slnx
dotnet build SagaEcommerce.slnx --no-restore
dotnet test SagaEcommerce.slnx --no-build
```

Integration tests spin up real, disposable Postgres/MongoDB/Redis/Kafka containers via Testcontainers — no shared state, no manual setup.

## Repository layout

- `apps/src` — the seven services and `BuildingBlocks` (shared contracts, telemetry, resilience pipelines).
- `apps/tests` — unit tests and Testcontainers-backed integration tests.
- `compose/` — the full local infrastructure and application stack.
- `kubernetes/` — production-style manifests: base resources, an Argo CD-managed overlay, cluster policies.
- `load-tests/k6` — versioned k6 workload profiles and thresholds.
- `observability/` — Collector, Prometheus rules, Alertmanager, Tempo, Loki, Grafana dashboards.
- `scripts/` — repeatable build, deploy, and verification workflows.
- `docs/` — one dated report per milestone, organized by topic.

## Beyond Compose: Kubernetes + GitOps

`kubernetes/` contains the manifests this project's own deployment runs from — a K3s cluster with Argo CD reconciling `kubernetes/overlays/local` from `main`. To point Argo CD at your own fork, see [`kubernetes/argocd/application.yaml`](kubernetes/argocd/application.yaml) and [`docs/gitops`](docs/gitops/). This path assumes a real cluster and is optional — the Compose quickstart above is the fastest way to see the whole system running.
