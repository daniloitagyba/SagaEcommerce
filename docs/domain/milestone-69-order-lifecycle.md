# Milestone 69: The Order's Life Does Not End at Confirmed

## What was wrong

Three statuses — `Created`, `Confirmed`, `Cancelled` — and every transition was its own hardcoded pair inside `OrderStatusStore`. Real orders continue: picked, shipped, delivered. This lab's simply stopped.

The most telling symptom was an outcome the saga had been logging since Milestone 43 with nowhere to put it:

```csharp
var outcome = reply.Committed ? "Confirmed" : "ConfirmedButCommitFailed";
```

`ConfirmedButCommitFailed` means payment was approved but the inventory commit came back negative — the customer is owed something the warehouse cannot supply. The order was then set to plain `Confirmed`, making it **indistinguishable from a healthy order** in every query, dashboard and read model. The information existed only in a log line.

## The lifecycle, as a table

```
Created ──► Confirmed ──► Picking ──► Shipped ──► Delivered
   │            │  ▲          │
   │            ▼  │          │
   │      FulfillmentHold ────┘
   ▼            │
Cancelled ◄─────┴──────────────┘   (from Created, Confirmed, Picking or FulfillmentHold)
```

`OrderStatuses` holds the legal predecessors of each status as data, not a switch. Inverted deliberately: that is the direction the compare-and-set needs, since it asks "may I move *to* here, given where the row currently is?" and can then guard on the whole set in one statement.

```sql
UPDATE orders SET status = @status
WHERE id = @id AND status = ANY(@allowed_from)
RETURNING coupon_code, payment_method, status;
```

`ANY(...)` is what makes "cancel this, from wherever it legitimately is" a single statement now that cancellation is reachable from four states. The alternative — a round trip per candidate, or read-then-write — reintroduces exactly the race the CAS exists to remove.

Two rules worth stating because they cost money if wrong:

- **A shipped order cannot be cancelled.** Cancelling after dispatch would void an authorization for goods already in a van. A shipped order is reversed by a *return* (Milestone 70), not a cancellation.
- **Nothing escapes a terminal state**, and nothing transitions *into* `Created` — it is the state an order is born in, not one it can return to.

## Capture moved to shipment

Milestone 68 captured at `Confirmed` only because `Shipped` did not exist. It now captures at `Shipped`, which is the entire reason for holding an authorization rather than charging at checkout.

The policy lives in one place (`OrderStatuses.SettlementActionFor`) because two services act on it, with deliberately different mechanisms:

| | Trigger | Mechanism | Why |
| --- | --- | --- | --- |
| Orders.Worker | saga-driven `Confirmed`/`Cancelled` | direct produce | already inside an async message handler |
| Orders.Api | operator-driven `Picking`→`Delivered` | transactional outbox | on an HTTP path — a capture command must not survive a rolled-back `Shipped` |

Duplicating the policy alongside the mechanism is what would let the two drift.

## Fulfilment is an endpoint, not a timer

`POST /orders/{id}/fulfillment` with `{"status": "..."}`. Fulfilment is driven by an external actor — a picker scanning a tote, a carrier webhook, an ops user resolving a hold — so it gets a real integration point rather than a background loop pretending orders ship themselves. Automating it on a schedule would have been less code and demonstrated nothing.

Three distinct refusals, because they mean different things to an operator:

- **422** — the status is not one an order can be moved into at all (typo, invented state).
- **409** — the move is legal in general, but this order is not in a state it can be made from. The response says which states it *could* have come from.
- **404** — no such order. Distinguished from 409 deliberately: telling an operator their order does not exist when it merely already shipped is the wrong answer.

## The timeout sweeper finally acts

Since Milestone 43 a timed-out saga was logged and dropped — the order stayed `Created` forever. Honest about the orchestrator noticing, useless to the customer looking at it.

It now cancels the order, which became *possible* rather than merely desirable once the transition table existed: cancelling mid-flight means cancelling from whichever state the order happens to be in, and the old single-predecessor CAS could not express that. Cancelling also releases the coupon and voids any card hold, because those hang off the transition rather than off the caller.

Still deferred: releasing the *inventory* reservation held by the timed-out step. That needs a compensating command per step rather than a status change.

## Three bugs the expanded lifecycle exposed

Every one of these compiled cleanly, passed the unit tests, and was only found by running the thing.

### 1. A missing DI registration silently killed the entire outbox

`Orders.Api` registers `IProducer<string, byte[]>` — `OrderCreated` is Avro. The new settlement-command publisher asked for `IProducer<string, string>` (the commands are JSON), and Confluent's producer is generic over its value type, so there is no single producer that serves both.

The failure mode is what makes this worth recording: the publisher is a dependency of the outbox *dispatcher*, so the dispatcher could not be constructed, so **no outbox message of any type was published** — including `OrderCreated`. The service reported healthy. Orders sat at `Created` and the saga never ran. The only evidence was an exception logged once per poll interval.

Fixed by registering the second producer, and then guarded systemically: `ValidateOnBuild`/`ValidateScopes` are now on in Orders.Api, Orders.Worker and Payments.Service. A missing registration now refuses to start the service instead of quietly disabling a background loop. It is off by default outside Development; the cost is a slower boot, which is the right trade against an outbox that looks fine and delivers nothing.

### 2. The fulfilment API never invalidated the cache

Transitions returned `200`, the database showed `Delivered`, and `GET /orders/{id}` kept answering `Confirmed` for the whole cache TTL. Every status change had previously happened in Orders.Worker, which has its own invalidator — so `IOrderCache` only ever needed to read.

Fixing it surfaced a subtlety worth getting right: the invalidation deletes the cached **value only**, never the fence sequence. The sequence (Milestone 48) is a monotonic `INCR`, so the stale `:fence` entry left behind cannot reject the refill — the next reader always draws a higher token. Deleting the sequence would be the actively dangerous choice: it restarts at 1, so a paused writer still holding token N could land after the delete and then reject every subsequent refill as "older" until the TTL expired. That is the precise stale-holder hazard fencing exists to prevent, reintroduced by over-cleaning.

### 3. A confirmed coupon redemption could never be released

Milestone 67 guarded the release on `state = 'Reserved'`, which was correct while an order settled exactly once — `Created → Confirmed` **or** `Created → Cancelled`, never both.

The fulfilment states break that assumption. An order confirmed and then cancelled had already moved its redemption to `Confirmed`, so the release silently did nothing: the slot stayed spent for an order that no longer existed, and the counter never came back. The guard now accepts `Reserved` or `Confirmed`, and still excludes `Released` — `redemption_count` is incremented once at reservation and untouched by confirmation, so exactly one decrement is owed regardless of which state it is released from.

The API-driven cancel also had to learn to release the coupon at all; it rides the same transaction as the status change, which is *better* than the worker's after-commit settlement because the coupon lives in the same database as the order.

## Verification

### Local

180 tests pass (up from 158). The 22 new ones cover the transition table, including a property-based check that walking **any** legal sequence of transitions never asks to both capture and void the same order, nor to do either twice.

### Against the real stack

**The full card lifecycle**, showing capture at the right moment:

| Step | Order status | Payment |
| --- | --- | --- |
| after saga | `Confirmed` | `Authorized` — hold open, **not** captured |
| `Shipped` directly | `409` | — (cannot skip the warehouse) |
| `Picking` | `Picking` | `Authorized` |
| `Shipped` | `Shipped` | **`Captured`** |
| `Delivered` | `Delivered` | `Captured` |
| cancel a delivered order | `409` | unchanged |
| invented status | `422` | unchanged |

**Void, finally reachable.** Milestone 68 wired the void path but could not exercise it end to end: in that saga a declined payment and a cancelled order coincided, so the payment was `Declined` and there was no hold to release. An order can now be confirmed and *then* cancelled:

```
SAVE10 redemption_count:  1
  → order placed (Card + SAVE10)     → 2, payment Authorized
  → POST .../fulfillment Cancelled   → 200
  → order Cancelled, redemption Released, payment Voided (order cancelled)
SAVE10 redemption_count:  1          ← the slot came back
```

## Stated simplifications

- **`FulfillmentHold` has no resolution workflow.** It is reachable, queryable and can be moved back to `Picking` or `Cancelled`; deciding *which* is a human's call, and there is no UI for it.
- **No partial fulfilment.** An order ships whole or not at all, matching the saga's single-line reservation.
- **The fulfilment endpoint is authorised as `orders:write`**, the same role that creates orders. A real deployment would separate warehouse operators from customers.
