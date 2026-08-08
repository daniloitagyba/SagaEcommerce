# Roadmap: Milestones 81-90

An audit of the system as of Milestone 80, the loose ends it found, and a
phased plan to close them.

Unlike every other document under `docs/`, this one is written *before* the
work rather than after it. It will be superseded by the per-milestone
reports it plans.

> **Status**: Phases 1-4 (Milestones 81-89) are implemented - see each
> milestone's own report, linked from `docs/README.md`, for what was
> actually built, what was verified, and what was deliberately left out.
> Every one of those reports was written after a build with 0 warnings/0
> errors and, where applicable, passing unit/property tests; none of them
> reached a live cluster (no Docker was available in the environment this
> work was done in), which each report states explicitly rather than
> implying otherwise. Phase 5 (Milestone 90) was not attempted.

## How this audit was done

Read end to end: the seven services' endpoints, `Orders.Domain`, the saga
orchestrator and reply consumer, `OrderStatusStore`, `PaymentSettlement*`,
`InventoryReservationMessageProcessor` and its backorder half, the pricing
engine, and the auth setup. Every finding below is anchored to code, and
every claim about what is *missing* was checked by grepping for an emitter
or a caller, not inferred from the milestone reports.

The system is in good shape. The gaps below are concentrated in three
places, and they cluster for a reason: **the saga's happy path and its own
compensations are extremely well covered, while the paths an operator or a
shopper drives from outside the saga are not.** The saga knows how to undo
itself. Nothing else does.

---

## Part 1 - Loose ends

### Severity 1: money and stock leak on cancellation

The three findings in this section are the same bug wearing three hats:
`Cancelled` is reachable from five states (`OrderStatuses.AllowedPredecessors`),
but the compensation that runs on the way there was written for exactly one
of them - the pre-confirmation saga path.

#### 1.1 Cancelling a Pix order keeps the shopper's money

`OrderStatusStore.RunSideEffectsAsync` (`apps/src/Orders.Worker/OrderStatusStore.cs:139`)
returns early for any method where `PaymentMethods.RequiresCapture` is
false:

```csharp
if (paymentMethod is null || !PaymentMethods.RequiresCapture(paymentMethod))
{
    return;
}
```

Pix is the only such method - and `Payment.Authorize`
(`apps/src/Payments.Service/Domain/Payment.cs:96`) lands an approved Pix
directly in `Captured`. The money has already moved. So the one payment
method where cancellation *must* produce a refund is precisely the one
where the code returns before reaching the settlement block.

`PaymentRefundRequested` is emitted from exactly one place in the repository
- `EfOrderReturnRepository:87`, the returns path - and returns require
`Status == "Delivered"` (`Order.TryReturn:74`). A Pix order cancelled at
`Confirmed`, `Picking`, `Backordered` or `FulfillmentHold` therefore has no
route to a refund at all.

The comment above the guard ("asking Payments to settle a Pix payment is
harmless but achieves nothing") is true for *capture* and false for *void* -
the two cases were collapsed into one condition.

**Fix.** Split the guard by action rather than by method. Capture stays
gated on `RequiresCapture`; the `Cancelled` branch dispatches on the
payment's actual state: `Authorized`/`AwaitingPayment` → void,
`Captured` → refund the full amount. `Payment.TryRefund` already guards
the cumulative amount, so a redelivered cancel refunds nothing extra.

#### 1.2 Cancelling after `Confirmed` never returns the stock

`InventoryReservationReleaseRequested` is published from two places only -
`OrderSagaReplyConsumer` (lines 149 and 227, both *before* the commit step)
and `SagaTimeoutSweeper:130`. `InventoryRestockRequested` is published from
one place - `EfOrderReturnRepository:113`, again the `Delivered`-only
returns path.

Inventory is committed when the order reaches `Confirmed`
(`ProcessCommitAsync` → `InventoryItem.TryCommit`, which draws the units
out of inventory entirely). From that moment, `Cancelled` is still legal
from `Confirmed`, `Picking` and `FulfillmentHold` - and nothing on that
path tells Inventory anything. The units are gone from stock, permanently,
for an order that will never ship.

This is worse than an accounting error: it is silent, it accumulates, and
the only signal is stock that drifts below what the warehouse physically
holds until someone counts the shelves.

**Fix.** `RunSideEffectsAsync`'s `Cancelled` branch emits
`InventoryRestockRequested` per line, through Orders' existing outbox, in
the same transaction as the status transition. Restock rather than release,
because the commit has already happened - release targets a reservation
that no longer exists, and `TrySettleReservationAsync` would find nothing.
Inventory's restock path is already idempotent through the inbox.

This requires the cancel path to know the order's lines. `OrderStatusStore`
currently reads `couponCode`, `paymentMethod`, `customerId` and `amount`;
it needs the lines too - the same widening `EfOrderStatusRepository` already
did once for the coupon.

#### 1.3 Cancelling a `Backordered` order leaves a live backorder behind

`Backordered → Cancelled` is legal. The backorder row lives in Inventory's
database and nothing in the cancel path removes it. `ReleaseBackordersAsync`
(`InventoryReservationMessageProcessor.Backorders.cs`) walks pending
backorders FIFO on every restock and reserves stock for them - including for
an order that was cancelled hours ago. The saga replies against a
`SagaOrchestrationState` row that is gone, the reserved units are never
released, and `BackorderTimeoutSweeper` cannot help because the backorder
was already removed by the release.

Worse, it is head-of-line: strict FIFO means a cancelled backorder at the
front of the queue consumes the restock that the next real shopper was
waiting for.

**Fix.** A `BackorderCancellationRequested` command on the cancel path,
consumed by Inventory, deleting the row inside the same inbox-guarded
transaction the other four handlers use. Alternatively - cheaper, and worth
weighing - have `ReleaseBackordersAsync` skip and delete backorders whose
order is no longer live, which requires Inventory to know the order's status
and is the wrong direction for a service boundary. Prefer the command.

### Severity 2: no end-user identity anywhere in the system

Milestone 26 is explicit that it built *application* identity - Keycloak
`client_credentials`, two realm roles, `orders:read`/`orders:write`. That
was the right scope for that milestone. What has not happened since is the
other half, and eight milestones of domain work have been built on top of a
`customerId` that is a string the caller picks.

#### 2.1 `customerId` is self-asserted

`CreateOrderRequest.CustomerId` (`Orders.Api/Contracts/OrderContracts.cs`)
comes from the request body and is never compared against the token. Any
holder of `orders:write` can place an order as any customer - which means
spending another customer's loyalty tier discount
(`LoyaltyTierRule` matches on `request.Customer`), their per-customer
coupon allowance (`HALFOFF`, 1 per customer), and their payment history for
risk scoring.

#### 2.2 No ownership check on any read or write

`grep -rn "HttpContext.User\|ClaimsPrincipal" apps/src` returns exactly one
hit, and it is the Keycloak role-unpacking handler in
`Orders.Api/Program.cs:130`. Nothing anywhere reads the caller's identity to
decide *which rows* they may see.

Concretely, any principal with `orders:read`:

- `GET /orders/{id}` - reads any order, including its shipping address, its
  line items and its full pricing breakdown.
- `GET /orders/summary` - reads **every customer's orders**, unpaginated
  beyond a `limit`, with no customer filter available even as an option
  (`OrderSummaryEndpoints.ListAsync` takes `status` and `limit` only).
- `GET /orders/{id}/history` - the full event-sourced history of any order.

And with `orders:write`: `POST /orders/{id}/returns` on any order, and
`POST /orders/{id}/fulfillment` on any order - which is the route to
`Cancelled`.

This is OWASP API1:2023 (Broken Object Level Authorization), and
`/orders/summary` is the mass-disclosure variant of it.

#### 2.3 The BFF launders unauthenticated requests into authorized ones

`Storefront.Service` holds a `KeycloakTokenProvider` and injects a service
account token on the way to Orders.Api
(`StorefrontEndpoints.CheckoutAsync:229`, `ProxyEndpoints.cs:26`). The
comment says this exists "so the browser never needs to know Orders.Api
requires auth" - accurate, and it is also the reason the browser never needs
to prove anything at all. `cartId` and `customerId` arrive from the client
and are forwarded as fact.

So the security boundary Milestone 26 built is real, and every request that
matters arrives on the trusted side of it.

**Fix.** This is one milestone's worth of work and it unblocks several
others:

1. An `orders-storefront` public client in the realm with the authorization
   code + PKCE flow, so the shopper gets their own token.
2. The BFF validates the shopper's token and derives `customerId` from `sub`
   rather than accepting it. The service-account token stays for the
   machine-to-machine hop, carrying the shopper identity forward as a
   verified claim (or, better, forwards the shopper's token directly and
   lets Orders.Api validate it - one fewer trusted intermediary).
3. Orders.Api gains an ownership filter: every order route compares the
   order's `CustomerId` against the caller's subject, unless the caller
   holds a new `orders:admin` role. `/orders/summary` filters by subject by
   default and requires `orders:admin` to query across customers.
4. `POST /orders/{id}/fulfillment` moves to `orders:admin` outright - a
   shopper is not a warehouse.

#### 2.4 Three services have no authentication at all

`Cart.Service`, `Catalog.Service` and `Inventory.Service` register no
authentication or authorization (`grep -n "AddAuthentication" Program.cs`
returns nothing for any of them). That is defensible for the read paths
behind a mesh policy. It is not defensible for these:

- `POST /products` and `POST /categories` (`Catalog.Service`) - unauthenticated
  catalog writes. Anyone who can reach the pod can add a product at any
  price, which is then priced into a real order by `OrderPricingService`.
- `GET/PUT/DELETE /carts/{cartId}` (`Cart.Service`) - `cartId` is an opaque
  string with no ownership concept. Enumerate it and you can read or empty
  anyone's cart.
- `GET /inventory` (`Inventory.Service`) - exposes exact
  `AvailableQuantity`/`ReservedQuantity` for the whole catalog, unauthenticated.
  Commercially sensitive; a competitor's scraper reads your sell-through
  rate.

**Fix.** Catalog writes move behind a `catalog:admin` role. Cart keys become
derived from the shopper's subject rather than supplied
(`cart:{sub}`), which removes the enumeration surface entirely rather than
guarding it. Inventory's list endpoint moves behind a role; the per-SKU
endpoint stays open but returns a coarse availability band (`InStock`,
`Low`, `OutOfStock`) to unauthenticated callers rather than an exact count -
which is what the storefront actually needs from it.

### Severity 3: the shopper-facing checkout can't reach the domain that was built for it

`Storefront.Service` is the only path a browser has. `CheckoutOrderRequest`
(`StorefrontEndpoints.cs:152`) carries exactly three fields:
`CustomerId`, `Items`, `CouponCode`.

Orders.Api accepts more than that, and the last four milestones of domain
work live in what the BFF does not send:

| Field | Accepted by Orders.Api | Sent by the BFF | Consequence |
|---|---|---|---|
| `shippingAddress` | yes (M71) | **no** | Every storefront order falls back to `FlatShippingAmount` and the global tax rate. `ShippingByPostalPrefix` and `TaxRateByRegion` are dead config on this path. `ADDRESS_MISMATCH` (M73) can never fire. |
| `paymentMethod` | yes (M68) | **no** | Every storefront order is Pix. Authorize/capture, boleto, and the whole M68/M73 payment state machine are unreachable. |
| `Idempotency-Key` header | yes (`OrderEndpoints.cs:34`) | **no** | A double-clicked checkout button creates two orders, charges twice, and reserves the stock twice. |

The idempotency one is the sharpest. The infrastructure is built, tested,
and behind a feature flag - and the one caller that is a human with a mouse
doesn't use it.

**Fix.** Widen `CheckoutRequest`/`CheckoutOrderRequest` to carry the address
and the payment method, and have the BFF generate a deterministic
`Idempotency-Key` for the checkout - derived from `(cartId, cart version)`
so a retry of the same cart replays and a genuinely new checkout does not.
The cart has no version today; a monotonically incremented Redis field per
mutation gives it one cheaply.

### Severity 4: returns refund less than was charged

`Order.TryReturn:106` computes the refund from `line.LineTotal` - the line's
share of the subtotal net of discount. Correct as far as it goes, and
property-tested to 10,000 cases (`ReturnRefundTests`).

But an order's `Amount` is `Subtotal - DiscountTotal + ShippingTotal +
TaxTotal`. The tax charged on the returned units, and the shipping charged
to deliver them, are never refunded. A fully-returned order refunds
`Subtotal - DiscountTotal` and keeps `ShippingTotal + TaxTotal`.

In Brazil this is not a rounding argument. Under the CDC's seven-day
*direito de arrependimento* (art. 49), a regret return owes the shopper the
full amount **including outbound shipping**. A defect return owes the same.
Only a discretionary return outside those cases can arguably keep the
shipping.

**Fix.** Prorate tax across lines the way `PricingAllocation.AllocateDiscounts`
already prorates discounts - same largest-remainder approach, same
whole-centavo invariant - and refund the returned units' tax share alongside
their line share. Shipping becomes a policy decision driven by a
`ReturnReason` the request already almost carries: `Defect` and `Regret`
(within the window) refund shipping in full on a complete return;
`Unwanted` outside the window does not.

### Severity 5: smaller things worth naming

- **The cart's price snapshot is silently discarded.** `CartLineItem`'s
  comment says checkout "is where prices get revalidated against the current
  catalog." It is - `OrderPricingService` re-prices against the live catalog
  - but nothing compares the new price to the snapshot and nothing tells the
  shopper. A shopper who added an item at 89,90 can be charged 129,90
  without a word. Real storefronts stop and ask. The data to detect it is
  already in the cart.
- **`WarehouseReplenishmentNeeded` has no consumer.** M73 says this
  explicitly and honestly. It remains true: the event is durable and
  nothing acts on it.
- **`GET /orders/summary` has no pagination.** `limit` only - no cursor, no
  offset. It will not survive a real dataset even after the ownership filter
  narrows it.
- **No customer-facing cancellation.** Cancelling requires
  `POST /orders/{id}/fulfillment {"status":"Cancelled"}` with
  `orders:write`. That is the warehouse's endpoint. A shopper cancelling
  their own order before it picks is the single most common e-commerce
  self-service action and there is no route to it.

---

## Part 2 - Distributed-systems concepts, applied to e-commerce

### Already covered, and covered well

Sagas (both styles), transactional outbox/inbox, idempotent consumers,
at-least-once delivery, event sourcing, CQRS with an async projection, CDC,
schema evolution, leader election, fencing tokens, distributed rate
limiting, hedged requests, load shedding, backpressure, circuit breakers,
bulkheads, chaos and partition game days, clock skew, linearizability
testing, read-your-writes, deterministic simulation, TLA+ model checking,
Kafka quorum durability, EOS transactions, DLQ redrive.

That is a genuinely unusual amount of ground. The concepts below are the
ones that are *specifically* e-commerce-shaped and not yet here.

### 1. CRDTs for the cart - the multi-device merge

The cart is a Redis hash with last-write-wins per field
(`CartStore.UpsertItemAsync`). A shopper with a phone and a laptop, or one
who goes offline in a lift and comes back, loses writes silently. This is
the canonical CRDT problem and carts are the canonical CRDT example
(Amazon's Dynamo paper opens with exactly this).

The interesting part for this lab is that the naive answer is *wrong* in a
way that is easy to demonstrate: an OR-Set makes "remove an item" lose to a
concurrent "add", which is the Dynamo resurrected-item bug. The correct
shape is per-SKU quantity as a PN-Counter with add-wins removal tracked by a
tombstone, or a delta-state LWW-element-set with the causal metadata that
makes removal stick.

**Why it fits here:** it needs no new infrastructure (Redis holds the state
already), it is provable with a property test the same way M66's pricing
invariants are, and it produces a demo - two clients, a partition, a merge -
that reads clearly.

### 2. Escrow / sharded counters for hot SKUs

Milestone 51 measured the ceiling: one SKU maps to one Kafka partition, and
correctness for concurrent reservations against the same SKU depends
entirely on that serialization (the comment at
`InventoryReservationMessageProcessor.cs:89` is explicit about it). That is
correct and it is also the throughput limit for a flash sale, where a single
SKU absorbs the entire load.

The classic answer is **escrow**: split the available quantity into *N*
independently-lockable buckets, route a reservation to a bucket by hash,
and only fall back to the slow global path when a bucket runs dry
(re-balancing from siblings). Reservations against the same SKU then
parallelize *N*-fold while the aggregate invariant still holds.

**Why it fits here:** this project already has the measurement (M51), the
multi-warehouse allocation model that escrow generalizes
(`WarehouseAllocationStore`), and the load-test harness to prove the
speedup. It is the natural sequel to M51 rather than a new topic.

### 3. Anti-entropy across service boundaries

Milestone 76 built *one* reconciliation - settlement replies driving
`FulfillmentHold`. That is reactive: it fires when a message arrives. What
is missing is the periodic, proactive kind that catches divergence no
message reports, which is exactly the class of bug findings 1.1-1.3 above
belong to.

Three cross-service invariants worth sweeping:

- Every `Confirmed`-or-later order has a `Captured`, `Authorized` or
  `AwaitingPayment` payment. (Catches 1.1.)
- Every committed inventory allocation belongs to a non-`Cancelled` order.
  (Catches 1.2.)
- Every pending backorder belongs to a live order. (Catches 1.3.)

Run as a leader-elected sweep emitting a metric per divergence class, with
an alert on non-zero - the same shape as M79's DLQ and outbox-backlog
alerts. The point is not that it fixes the bugs; it is that it makes the
next one of this class visible in minutes instead of at inventory count.

### 4. Admission control by customer class

M11 sheds load and M54 handles backpressure, both uniformly. E-commerce has
an obvious priority signal that the system already models: `CustomerTiers`.
Under overload, shedding a Gold member's checkout and admitting an anonymous
catalog browse is the wrong trade, and the machinery to distinguish them is
already in the database.

Weighted fair queueing or per-class token buckets on the Orders.Api rate
limiter, with the class taken from the (now-verified, per Part 1) identity.

### 5. Bitemporal pricing

An order stores the price it was charged (`OrderLine.UnitPrice`), which is
the important half. What cannot be answered today is "what *would* this have
cost on 3 August?" - because `PricingOptions` is current config with no
history. Promotions with validity windows (Part 3) make the pricing engine
bitemporal almost as a side effect: price as of *transaction time* versus
*valid time*, which is what makes a disputed charge auditable.

### 6. Worth naming, lower priority

- **Follower reads / geo-replication** for the catalog - MongoDB replica set
  exists (M49); read preference by region does not.
- **Probabilistic structures** - a Bloom filter in front of the catalog
  cache to stop penetration by nonexistent SKUs; HyperLogLog for unique
  viewers per product, which pairs with M44's bestseller sorted sets.
- **Causal delivery across aggregates** - the outbox guarantees per-message
  durability, not that a customer's two orders are projected in order.

---

## Part 3 - E-commerce business rules: what is modelled, what is not

| Area | Modelled | Missing |
|---|---|---|
| **Catalogue & price** | Server-side pricing, price never trusted from the client (M66); cart price snapshot | Price validity windows; per-region currency |
| **Promotions** | Stacking, cap at subtotal, per-line proration, coupon lifecycle with per-customer limits (M67), loyalty tier (M71) | **Validity calendar** (start/end); **exclusivity** ("não cumulativo"); **priority / best-of** selection; **campaign budget cap** |
| **Cart** | Redis as system of record, TTL, price snapshot (M42) | Multi-device merge; cart-level reservation (soft hold) |
| **Availability** | Multi-warehouse allocation, reorder point, backorders with FIFO release and timeout (M72/M74) | Replenishment consumer; safety stock; available-to-promise by date |
| **Checkout** | Idempotency key, risk scoring with five signals (M66/M73) | Freight quotation by weight/dimension (only a postal-prefix table); address validation |
| **Payment** | Authorize/capture, Pix/Card/Boleto, expiry sweeper, partial refunds, settlement reconciliation (M68/M70/M73/M76) | **Installments (parcelamento)** - the single most Brazil-specific gap; manual review queue for mid-risk scores; partial capture |
| **Fulfilment** | Explicit state machine with compare-and-set transitions, `FulfillmentHold` for human attention (M69/M76) | Carrier / tracking code; split shipment; delivery estimate |
| **Post-sale** | Partial returns, prorated refunds, restock (M70) | **Regret window (CDC art. 49)**; exchange as distinct from return; RMA authorization step; tax/shipping on refunds (see 4 above) |
| **Customer** | Tiers earned on confirmation, geography, account age (M71/M73) | **End-user identity** (see 2 above); address book; guest checkout |

The three in bold are the ones a real Brazilian storefront could not launch
without.

---

## Part 4 - The plan

Ordered by dependency, not by interest. Phase 1 is bug-fixing on live money;
Phase 2 unblocks everything customer-facing; Phases 3-5 are the milestones
this lab exists to write.

### Phase 1 - Correctness on money and stock

**Milestone 81: Cancellation gives back everything it took**

Closes 1.1, 1.2, 1.3 together, because they are one bug.

- `OrderStatusStore.RunSideEffectsAsync` dispatches the `Cancelled` branch
  on payment *state* rather than payment *method*: void when awaiting
  settlement, refund when captured.
- The same branch emits `InventoryRestockRequested` per line through the
  outbox, in the status-transition transaction.
- A `BackorderCancellationRequested` command clears Inventory's backorder
  row.
- `EfOrderStatusRepository` widens its projection to carry the order's
  lines, the way it already carries the coupon.

*Proof*: a Pix order cancelled from `Confirmed` shows `Refunded` in Payments
and restored `AvailableQuantity` in Inventory; a `Backordered` order
cancelled and then restocked does not consume the restock. Add the
cancellation paths to the TLA+ model - the current model covers the saga's
own compensation, not the operator-driven cancel, which is precisely the
gap that let this ship.

**Milestone 82: A refund is the whole charge, not the line**

- Prorate `TaxTotal` per line at checkout and store it, mirroring
  `LineDiscount`. Computing it at return time from a rate that may have
  changed is the same mistake M70 avoided for discounts.
- `ReturnReason` becomes an enum (`Defect`, `Regret`, `Unwanted`), with
  the regret window configurable and defaulting to seven days.
- Shipping refunds on `Defect`, and on `Regret` inside the window, for a
  complete return only.
- Extend `ReturnRefundTests`' property suite: no sequence of partial returns
  may refund more than `Order.Amount`.

### Phase 2 - Identity

**Milestone 83: The shopper stops being a string**

The largest of these and the one with the widest blast radius.

- `orders-storefront` public client, authorization code + PKCE.
- `Storefront.Service` validates the shopper's token; `customerId` derives
  from `sub`.
- Ownership filter on every order route in Orders.Api. New `orders:admin`
  role for cross-customer reads and for `/fulfillment`.
- `GET /orders/summary` filters by subject by default; keyset pagination on
  `(created_at, id)` while the query is being rewritten anyway.
- New `POST /orders/{id}/cancellation` - the shopper's own route, legal only
  from `Created`/`Confirmed`/`Backordered`, reusing M81's compensation.

*Expect this to break things*: the Pact contracts, the k6 profiles, the
smoke tests and the seeded `customer-42` all assume a caller-supplied
customer. That churn is the honest cost of having deferred it.

**Milestone 84: Every service gets a door**

- Catalog writes behind `catalog:admin`.
- Cart keys derived from `sub`, not supplied - the enumeration surface
  disappears rather than being guarded.
- Inventory's list endpoint behind a role; the per-SKU endpoint returns an
  availability band to unauthenticated callers.
- Tighten the Linkerd `AuthorizationPolicy` set to match, so both layers
  move together the way M26 established.

### Phase 3 - The checkout the domain deserves

**Milestone 85: The BFF carries what the domain already models**

- `shippingAddress` and `paymentMethod` through the storefront checkout.
- Cart version field in Redis; deterministic `Idempotency-Key` derived from
  `(cartId, version)`.
- Price-change detection: compare the re-priced total against the cart's
  snapshot, and return `409` with the old and new breakdown when they differ
  beyond a configurable tolerance, requiring an explicit re-confirmation.

*Proof*: a k6 profile that double-submits every checkout and asserts the
order count matches the shopper count.

### Phase 4 - The distributed-systems milestones

**Milestone 86: Carts that merge instead of overwrite** - Part 2 §1. The
demo is two clients partitioned from Redis, diverging, and merging without
resurrection.

**Milestone 87: Escrow for hot SKUs** - Part 2 §2. Baseline against M51's
measured single-partition ceiling; the milestone is only worth writing if
the speedup is measured, not assumed.

**Milestone 88: Anti-entropy across service boundaries** - Part 2 §3. Build
it *last* among these, deliberately: run it against the pre-M81 database and
it should report exactly the divergences M81 fixed. That is the test.

**Milestone 89: The replenishment loop closes** - a consumer for
`WarehouseReplenishmentNeeded` that raises a purchase order, receives stock,
and restocks - which is also what finally exercises the backorder release
path end to end from a real cause rather than a manual restock.

### Phase 5 - Business-rule depth

**Milestone 90: Promotions get a calendar, a priority and a budget**

- Validity windows on every promotion, evaluated against the order's
  `CreatedAt` - which makes the engine bitemporal (Part 2 §5).
- Exclusivity groups: promotions in the same group do not stack; the best
  one wins.
- A campaign budget that depletes, with the same race the coupon's last slot
  has (M67) and the same advisory-check-then-atomic-claim answer.

Installments (`parcelamento`) and carrier/tracking are the two other
significant domain gaps from Part 3. Both are milestone-sized in their own
right and neither blocks anything above; they belong after 90.

---

## Ordering rationale

Phase 1 before Phase 2 because a bug that loses money should not wait behind
a refactor that touches every test in the repository.

Phase 2 before Phase 3 because the BFF cannot stop trusting a
client-supplied `customerId` until there is something to replace it with,
and the price-confirmation flow in M85 needs a session to confirm *against*.

Phase 4 after Phases 1-3 because M88's whole value is running it against
the divergences M81 fixed, and M86's cart merge is easier to reason about
once cart identity is derived from the shopper rather than supplied.

Phase 5 last because it is the only phase where nothing is currently wrong -
only absent.
