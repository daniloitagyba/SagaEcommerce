# Milestone 67: Coupons That Can Actually Run Out

## The defect

Milestone 66 shipped coupons as configuration - a dictionary mapping a code to a percentage:

```csharp
public Dictionary<string, decimal> Coupons { get; init; } = new(...)
{
    ["SAVE10"] = 10m, ["SAVE20"] = 20m, ["HALFOFF"] = 50m
};
```

Nothing counted how many times a code had been used, by whom, or until when. `HALFOFF` took 50% off for anyone, any number of times, forever. A code that leaked - screenshotted, posted to a deals forum, shared between accounts - could not be stopped by anything short of a redeploy. Promotion abuse is the ordinary consequence, and this was the one gap in this lab's domain that was an outright **defect** rather than a missing feature: a coupon that cannot be used up is not a simplified coupon, it is a broken one.

## What a coupon is now

A row, with the things configuration could never express:

| | |
| --- | --- |
| `valid_from` / `valid_until` | A half-open window. The opening instant is already valid; the expiry instant is already expired. |
| `minimum_order_amount` | Checked against the gross subtotal. |
| `max_total_redemptions` | Null means unlimited - Milestone 66's behaviour, now opt-in rather than the only option. |
| `max_per_customer` | Enforced per customer, independently of the total. |
| `redemption_count` | Counts **reservations**, not completions. |

Rejections are reported with a specific reason instead of being silently dropped. Milestone 66 ignored unknown codes, which was defensible when a config typo was the only way to produce one; now that codes expire and run out, "why is this not applying?" is a question the shopper deserves an answer to.

## Redemption is a saga participant

The important design decision is not the columns - it is that a redemption has a life:

```
checkout ──reserve──► Reserved ──order Confirmed──► Confirmed
                          │
                          └──order Cancelled────► Released (slot returns to the pool)
```

Counting completions instead of reservations would let unlimited concurrent checkouts all pass the limit check before any of them finished. But counting reservations without ever releasing them is worse in the other direction: **every declined payment would burn a redemption permanently**, so a coupon limited to 100 uses would be exhausted by 100 failed checkouts without a single sale.

Release is what makes this a saga participant rather than a fire-and-forget increment, and it is the mirror image of Inventory's reservation release - the pattern Milestone 43 already established for stock, applied to a second kind of finite resource.

### The settlement hook

`OrderStatusStore`'s compare-and-set now returns the coupon code:

```sql
UPDATE orders SET status = @status
WHERE id = @id AND status = @expected_status
RETURNING coupon_code;
```

Both saga paths and the timeout sweeper can race to settle the same order. The CAS already decides which caller actually moved it; returning the coupon from the *same statement* means the redemption is settled by exactly the winner. A losing caller gets no row back, so it cannot double-count a confirmation or hand a slot back twice - no separate check, no window between deciding and acting.

The redemption is settled *after* the status commit rather than inside it. The order's own state is the thing that must not be lost; a redemption left `Reserved` is a recoverable accounting discrepancy, whereas failing inside the transaction would roll back a transition the rest of the saga has already acted on.

## Concurrency: the same problem as stock, a different mechanism

Enforcing `max_total_redemptions` under concurrent checkout is the same shape of problem as preventing oversell - a finite pool, concurrent claimants - but the mechanism has to differ, and the contrast is the point:

- **Inventory** serialises by Kafka partition key. Same-SKU requests are never processed concurrently in the first place, so `InventoryItem.TryReserve` needs no lock at all (see its class comment).
- **Coupons** are claimed on the synchronous HTTP checkout path. There is no partition to hide behind, so correctness comes from a single guarded UPDATE:

```sql
UPDATE coupons SET redemption_count = redemption_count + 1
WHERE code = @code
  AND valid_from <= @now AND valid_until > @now
  AND (max_total_redemptions IS NULL OR redemption_count < max_total_redemptions)
```

Postgres evaluates the guard and applies the increment as one operation, so exactly one racer affects a row and the rest see zero. The guard lives in the `WHERE` clause, never in application code - identical in shape to the status CAS above and to Inventory's stock reservation.

The **per-customer** limit rides on a side effect of that same statement: an `UPDATE` takes a row lock held until commit, so by the time the transaction counts that customer's existing redemptions, every competing redemption of the same coupon is blocked behind it. No second lock statement, and contention stays scoped to one coupon - the same way inventory contention is scoped to one SKU rather than the whole catalogue.

### Reserved in the order's own transaction

The slot is claimed in the same transaction that persists the order. It has to be: reserving separately and then failing to insert the order would leak a slot nothing would ever return, since the release path keys off the order reaching `Cancelled` and there would be no order to cancel.

### A lost race is a value, not an exception

`EfOrderRepository` reports a lost race back out of the Polly pipeline as a return value rather than throwing through it. The Postgres pipeline's retry has no `ShouldHandle` predicate, so it retries every exception **and feeds the circuit breaker with each one**. Throwing would mean an exhausted coupon got retried twice for nothing and, in a burst, could trip the breaker for every other Postgres caller in the service. The failure is real, but it is a business outcome, not a fault.

It surfaces as **409 Conflict**, not 400: nothing about the request was wrong, it simply arrived second.

## Pricing stayed pure

Coupons now live in a database, and the pricing rules must not touch it - the ten property-based tests depend on pricing being a deterministic function of its inputs.

So the coupon is **resolved before pricing, never during**. `PricingRequest` carries a `ResolvedCoupon` (code, description, percentage) that has already been looked up and found eligible; `CouponPercentageRule`'s only remaining job is arithmetic. The subtotal needed for the minimum-order check is just the sum of the lines and does not depend on the coupon, so this ordering costs nothing.

Eligibility itself (`CouponEligibility.Evaluate`) is a pure function, so the whole rule set is testable without a database - and property-testable, which is how the "a coupon with a limit is never accepted past it" invariant is checked across 10,000 generated combinations of window, subtotal, minimum and per-customer history.

## Verification

### Local

147 tests pass (up from 137): 10 new for coupon eligibility, including one property-based invariant. Existing pricing tests were updated to supply resolved coupons rather than codes.

### Against the real stack

**Rejection reasons**, each with a specific message rather than silence:

| Case | Result |
| --- | --- |
| Unknown code | `400` — "Coupon 'NAO-EXISTE' does not exist." |
| Below minimum (`SAVE20`, needs 200) | `400` — "This order does not reach the minimum amount…" |
| Above minimum | `201` — 4 × 74,90 = 299,60 → 20% off → **239,68** |
| Second use by the same customer (`HALFOFF`, limit 1) | `400` — "You have already redeemed coupon 'HALFOFF' the maximum number of times." |
| Same coupon, different customer | `201` — per-customer limits are per customer |

**Release on cancellation**, the behaviour the whole reservation model exists for:

```
SAVE10 redemption_count = 0
  → checkout (3.654,91, new customer)  → redemption Reserved, count = 1
  → risk score 70 → payment declined   → order Cancelled
  → redemption Released                → count back to 0
```

**Concurrency**, 40 simultaneous checkouts against a coupon with 5 slots:

```
201 Created (slot claimed):   5
409 Conflict (lost the race): 35
coupon_redemptions rows:      5
coupons.redemption_count:     5
```

**The test has teeth.** Temporarily replacing the guarded UPDATE with a read-then-write implementation and re-running produced:

```
201 Created:                  40      ← 8x over-redemption against a limit of 5
coupon_redemptions rows:      40
coupons.redemption_count:     3       ← lost updates: 40 increments landed as 3
```

Not merely "the limit didn't hold" - the counter and the rows disagreed by an order of magnitude, because concurrent read-then-write increments overwrote each other. That is the lost-update anomaly in its purest form, and it is what `scripts/coupon-redemption-concurrency-test.sh` exists to catch. The script also fails if fewer slots were claimed than the limit, so a run where the limit was never actually contended cannot pass trivially.

## Stated simplifications

- **Coupons are percentage-only.** Fixed-amount ("R$20 off") and free-shipping coupons are not modelled; the discount cap and allocation machinery would carry them unchanged.
- **No coupon admin API.** Coupons are seeded by migration. Creating and expiring campaigns at runtime is a CRUD surface, not a distributed-systems concern.
- **A reservation has no expiry of its own.** It is released when the order is cancelled, and the saga timeout sweeper guarantees an order cannot stay in-flight forever - so a slot cannot leak indefinitely, but it can be held for as long as the saga's timeout allows.
