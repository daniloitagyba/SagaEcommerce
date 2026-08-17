# Orders.Api

![Orders.Api architecture](../../../docs/images/services/orders-api.png)

The REST + gRPC entry point for placing, tracking, and managing orders. Prices every line item server-side against the live catalog — the client states what it wants, never what it costs — and hands the rest of the order's life (saga, settlement, projections) off to [`Orders.Worker`](../Orders.Worker/README.md) via the transactional Outbox.

## Responsibilities

- **Checkout** — `POST /orders` accepts SKU and quantity, pricing them server-side with promotions, coupons, and loyalty tiers via `Orders.Application`/`Orders.Domain`.
- **Durable idempotency** — an `Idempotency-Key` is scoped by customer and bound to a normalized request hash in PostgreSQL. Lookup happens before pricing; the key, order, coupon reservation, and Outbox row commit together. Reusing the key with another payload returns `409 Conflict`.
- **Fulfilment** — `POST /orders/{id}/fulfillment` advances the order lifecycle (`Created → Confirmed → Picking → Shipped → Delivered`) through a single compare-and-set that also queues the implied settlement command (capture on `Shipped`, void on `Cancelled`).
- **Returns** — partial or full returns, refunding each line's actually-charged total (post-discount), never list price.
- **Reads** — order summaries from the CQRS read model and full history from the event store.

## Talks to

| Direction | What | Why |
|---|---|---|
| in | `Client` / `Storefront.Service` | REST, JWT-authenticated via Keycloak |
| out | PostgreSQL (`orders` db) | order + line items + Outbox row, one transaction |
| out | Redis | response cache and distributed rate limiting; never the authority for order creation |
| out | Kafka `orders.created.v1` | published by the Outbox dispatcher, never inline with the request |

## Run it

Part of the Compose stack — see the [repo root README](../../../README.md#quickstart-docker-compose). Two replicas (`orders-api-1`, `orders-api-2`) sit behind the `nginx` gateway on `127.0.0.1:8088` and serve REST on `:8080`.

## See also

- [Milestone 66 — line items, pricing, and risk](../../../docs/domain/milestone-66-line-items-pricing-and-risk.md)
- [Milestone 69 — order lifecycle](../../../docs/domain/milestone-69-order-lifecycle.md)
- [Milestone 70 — returns and refunds](../../../docs/domain/milestone-70-returns-and-refunds.md)
- [Milestone 48 — Redis fencing tokens](../../../docs/resilience/milestone-48-fencing-tokens.md)
