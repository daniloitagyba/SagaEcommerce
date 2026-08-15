# Domain and Business-Rules Review — Implementation Plan (2026-08-15)

Fifth audit in the current series, and the second to look at business
rules. The first
([`audit-2026-08-14-service-and-business-rule-review.md`](audit-2026-08-14-service-and-business-rule-review.md),
13 findings, closed in `84cd877`) worked outward from the *saga*: which
message paths existed, which replies nobody consumed, which compensations
never fired. It found real correctness bugs at the seams between services.

This pass works in the opposite direction — from the money outward. It
reads the aggregates and the arithmetic (`Order`, `OrderReturn`, `Coupon`,
`Customer`, `Payment`, `PricingModel`, `MoneyAllocation`, the NRules
promotion set) and asks a narrower question: **for each rule the domain
states, is there a code path that actually enforces it?**

That framing found a different class of defect from the first pass. Nothing
below is a race or a lost message. They are rules that are written down,
often carefully, and then not wired to anything — or wired to something
whose behaviour contradicts the comment above it.

**Method.** For every public method on the domain aggregates I resolved the
production call sites (excluding tests), then read each rule's doc comment
against what the calling code does. Where a comment states a policy
("cancelled or fully refunded ones must not buy standing"), I traced
whether any path implements it. Findings are ranked; the first two are the
ones worth acting on.

**Scope note.** The pricing arithmetic itself came back clean and I am not
going to pad this document by pretending otherwise — see *What is genuinely
solid* at the end for what I checked and why it holds.

---

## Executive summary

| # | Finding | Severity | Theme |
|---|---|---|---|
| 1 | `Customer.ReverseCompletedOrder` has no production caller — returns and cancellations both keep loyalty standing | P1 | Loyalty |
| 2 | A backorder that cannot be filled blocks every backorder behind it, and the timeout sweeper then cancels them | P1 | Inventory |
| 3 | The risk evaluator reads a customer's entire payment history on checkout's critical path | P2 | Payments |
| 4 | Which discount survives the 100% cap is decided by alphabetical code order | P3 | Pricing |
| 5 | The `Order` aggregate compares status against string literals, not `OrderStatuses` | P3 | Domain |

---

## 1. `Customer.ReverseCompletedOrder` has no production caller (P1)

`Orders.Domain/Customer.cs` states the rule twice, in two places, with
reasoning:

```csharp
/// <summary>Counts only completed orders - cancelled or fully refunded ones must not buy standing.</summary>
public decimal LifetimeSpend { get; private set; }
```

```csharp
/// <summary>
/// Reverses a completed order's contribution after a full refund. Tier
/// is deliberately <em>not</em> demoted here - taking a discount away
/// retroactively generates support tickets; real loyalty programmes
/// review downward on a schedule, not on the instant.
/// </summary>
public void ReverseCompletedOrder(decimal amount)
```

`CustomerTierStore`'s own header adds the motive:

> Runs on *confirmation*, not creation, or placing and cancelling would be
> the cheapest route to Gold.

**`ReverseCompletedOrder` is called from exactly one place in the
repository: `tests/Orders.UnitTests/CustomerTierTests.cs:57`.** There is no
production call site — not in `Orders.Api`, `Orders.Application`,
`Orders.Infrastructure`, or `Orders.Worker`. Neither of the two components
that record spend has a reversing counterpart at all:
`Orders.Worker/CustomerTierStore.cs` has only `RecordCompletedOrderAsync`,
and `Orders.Infrastructure/Persistence/EfOrderStatusRepository.cs` only
`RecordCompletedOrderForTierAsync`.

Two concrete consequences, both reachable with the deployed configuration:

**a) A fully returned order keeps its spend.** `ReturnOrderHandler` →
`EfOrderReturnRepository.SaveReturnAsync` persists the return, flips the
order to `Returned` when `markOrderReturned` is true (i.e. *fully*
returned — exactly the "fully refunded" case the doc names), queues the
refund and restock commands, and emits `OrderStatusChanged`. It never
touches `customers`. Neither status store handles `Returned` either:
`ApplySideEffectsAsync` branches on `Confirmed` and `Cancelled` only.

**b) Confirm-then-cancel still buys standing — the exact loophole recording
at confirmation was meant to close.** `Cancelled` is reachable from
`Confirmed` (`OrderStatuses.AllowedPredecessors[Cancelled]` includes it).
Both cancellation paths release the coupon slot and queue the payment
cancellation; neither reverses the tier contribution. So placing, letting
the saga confirm, then cancelling still adds to `lifetime_spend`
permanently. Recording at confirmation raised the cost of the loophole from
"free" to "one confirmed order" — it did not close it.

The business effect is a standing 7% discount (`CustomerTiers.DiscountPercentageFor(Gold)`)
on every future order, earned from spend that was refunded.

**Fix.** The domain method already exists and is tested; this is wiring,
not design.

1. Add `ReverseCompletedOrderAsync` to `Orders.Worker/CustomerTierStore.cs`
   as the mirror of `RecordSql` — decrement `lifetime_spend` and
   `completed_order_count` with the same `GREATEST(..., 0)` flooring
   `CouponRedemptionStore.ReleaseSql` already uses, and **do not** re-derive
   `tier` downward, matching the domain's stated policy.
2. Call it from `OrderStatusStore.ApplySideEffectsAsync` on `Cancelled`
   when the previous status was one where the contribution had already been
   recorded — the `RETURNING` clause already carries `customer_id` and
   `amount_cents`, so no extra read is needed. The status CAS returns the
   pre-write row, so "was it Confirmed or later" is answerable there.
3. Mirror the same two changes in
   `EfOrderStatusRepository` (the operator-driven path), which already
   duplicates `RecordCompletedOrderForTierAsync` for the same reason.
4. For the return path, call it from `SaveReturnAsync` when
   `markOrderReturned` is true, in the same transaction as the return and
   the refund command.

Decide explicitly and write it down: a **partial** return should almost
certainly *not* reverse anything (the customer kept most of the order), and
today's code has no partial-return tier logic at all — which is right, but
right by omission rather than by decision.

**Test it with the seam that already exists.** `CustomerTierTests`
covers `ReverseCompletedOrder` in isolation; what is missing is a test that
a full return, and a confirm-then-cancel, actually reach it.
`OrderStatusStoreTransactionTests` already asserts tier movement on
confirmation (`OrderStatusStoreTransactionTests.cs:123`) and is the natural
place for the cancellation case.

---

## 2. An unfillable backorder blocks every backorder behind it (P1)

`Inventory.Service/InventoryReservationMessageProcessor.Backorders.cs:31-46`,
run whenever stock arrives for a SKU:

```csharp
var pending = await dbContext.Backorders
    .Where(backorder => backorder.Sku == sku)
    .OrderBy(backorder => backorder.RequestedAt)      // FIFO
    .ToListAsync(cancellationToken);

foreach (var backorder in pending)
{
    var decision = await allocationStore.TryReserveAsync(
        backorder.ReservationId, sku, backorder.Quantity, now, cancellationToken);

    if (!decision.Reserved)
    {
        break;                                        // <-- stops the whole queue
    }
    ...
}
```

Reservation is all-or-nothing: `WarehouseAllocationStore.TryApplyReservationAsync`
returns `Refused` unless the plan is `Fulfillable` and every leg validates.
So a backorder for 10 units against a 3-unit restock is refused outright.

Combined with `break`, that means **a restock of 3 units fills zero
backorders** if the oldest one wants 10 — the 3 units sit available while
every order behind the head waits, including ones that want a single unit
and could be satisfied immediately.

It gets worse on the next tick. `BackorderTimeoutSweeper` gives up on
backorders past `BackorderOptions.TimeoutMinutes` and replies
`InventoryReservationReplied(Reserved: false, Backordered: false)`, which
`OrderSagaReplyConsumer` treats as a permanent refusal and cancels the
order. So the queue behind a large head-of-line backorder is not merely
delayed — **it is eventually cancelled, for stock that was physically
present and available the whole time.**

**This is a policy choice with no comment on it.** `break` is strict FIFO
fairness (nobody jumps the queue, the big order is not starved); `continue`
is best-effort fill (better utilisation, but a large backorder can starve
indefinitely behind a stream of small ones). Both are defensible. The file
documents the advisory lock, the reply reuse, and the cancellation path in
detail — and says nothing at all about this one line, which is the line
that decides whether a customer's order survives.

**Fix.** Decide the policy, then make the code say which one it is:

- **Keep strict FIFO**, and add the comment explaining that a large head is
  allowed to hold the queue deliberately. Then the timeout interaction
  needs addressing separately, because cancelling fillable orders behind a
  blocked head is not something FIFO fairness argues for.
- **Or switch to `continue`** with a starvation guard: skip past a
  head that cannot be filled, but stop skipping it once it has been
  passed over N times or has waited past some fraction of the timeout
  window, so it is guaranteed to get the next sufficient restock.

Recommend the second. It fills orders that can be filled, and the
starvation guard is what makes it fair rather than merely greedy. The
existing `BackorderTimeoutSweeperTests` (Testcontainers-backed, already
seeds backorders at controlled `RequestedAt` values) is the right place for
a test: two backorders, quantities 10 then 1, restock 3, assert the small
one is filled and the large one still pending.

---

## 3. The risk evaluator reads a customer's entire payment history (P2)

`Payments.Service/Risk/PaymentRiskEvaluator.cs:68-73`:

```csharp
var decidedDates = await dbContext.Payments
    .AsNoTracking()
    .Where(payment => payment.IsPrimary && payment.CustomerId == customerId)
    .Select(payment => payment.DecidedAt)
    .ToListAsync(cancellationToken);
DateTimeOffset? firstSeen = decidedDates.Count > 0 ? decidedDates.Min() : null;
```

Unbounded: every payment this customer has ever made, materialised into
memory, to compute a single `Min()`. The comment is honest about it —

> Narrow (one date per row, not the four columns below) rather than
> genuinely bounded

— and the reason given is real: `NEW_ACCOUNT`/`FIRST_PURCHASE` need the
account's true first-ever payment date, which a lookback window cannot
answer, and SQLite's EF provider (the unit-test stand-in) cannot translate
`MIN()` over a `DateTimeOffset` column.

But this runs on the payment-decision path of every checkout, and it is the
one query in this service whose cost grows without bound in the customer's
order count. The rest of the method is careful about exactly this: the
history query 30 lines below is `OrderByDescending(...).Take(HistoryMaxRows)`,
**with an explicit provider branch** for the same SQLite limitation:

```csharp
if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", ...))
{
    // client-side ordering for the test provider only
}
else
{
    // server-side ORDER BY + LIMIT against ix_payments_customer_history
}
```

The fix pattern is already in the file, immediately below the problem.

**Fix**, cheapest first:

1. Apply the same provider branch to `firstSeen`: `MIN(decided_at)` pushed
   down on PostgreSQL, client-side `Min()` retained for SQLite. One query,
   constant cost, no schema change, and it reuses an index that already
   exists.
2. If that is not enough later, denormalise `first_payment_at` onto the
   customer row — but do not start there; (1) removes the unbounded read
   entirely and is a dozen lines.

---

## 4. Which discount survives the 100% cap is decided by alphabetical order (P3)

`NRulesPricingEngine.Price` caps stacked discounts at the subtotal so
"two campaigns each granting 60% can't send the order total negative" — a
good invariant, correctly applied to `discountTotal`. To keep the receipt
honest it then shrinks the itemised list to match:

```csharp
var discounts = session.Query<AppliedDiscount>()
    .Where(discount => discount.Amount > zero)
    .OrderBy(discount => discount.Code, StringComparer.Ordinal)   // <-- the order that decides
    .ToList();
...
discounts = CapDiscounts(discounts, subtotal, currency);          // truncates from the end
```

`CapDiscounts` walks the list paying out discounts until `remaining` hits
zero, then stops. So when the cap binds, **which discounts appear on the
shopper's receipt at full value and which get truncated to zero is decided
by `StringComparer.Ordinal` on the discount code**: `BULK-*` and
`CATEGORY-*` sort before a coupon code like `SAVE10`, which sorts before
`TIER-*`. A coupon the shopper explicitly typed is more likely to be
zeroed than an automatic volume discount, purely because `B` < `S`.

**Not reachable with today's configuration**, and I want to be precise
about that rather than overstate it: the deployed maximum stack is
`HALFOFF` 50% + bulk 8% (`BulkDiscountPercentage`) + electronics 5%
(`CategoryDiscounts`) + Gold 7% (`CustomerTiers.DiscountPercentageFor`) =
70%. The cap is a defensive guard against a future campaign, not a live
bug. The ordering is also deterministic, so it is not a correctness
hazard — it is an unstated policy that will surface the first time someone
configures a campaign past 100%, as a support question ("why did my coupon
show as R$0,00?") rather than as an error.

**Fix.** Order the cap by intent rather than by spelling, and say so: pay
out shopper-presented discounts (the coupon) first, automatic ones after,
since the coupon is the one the shopper will notice missing. A comparator
keyed on a small `DiscountPriority` enum, applied only inside
`CapDiscounts` — the display ordering can stay alphabetical. Worth a
`PricingEngineTests` case configuring a >100% stack, which today has no
coverage at all.

---

## 5. The `Order` aggregate compares status against string literals (P3)

`Orders.Domain/Order.cs` hardcodes three status strings:

```
Order.cs:89   if (Status != "Delivered")   // TryReturn's "nothing can come back that never arrived" guard
Order.cs:169  Status = "Created",
Order.cs:229  Status = "Created",
```

Everywhere else in the system these come from `BuildingBlocks.OrderStatuses`
— the state machine, both status stores, the read model, the fulfilment
endpoints. The aggregate cannot reference it: `Orders.Domain.csproj` takes
**only** NodaMoney, deliberately, and the domain-purity fitness functions
enforce that. So the literals are forced, not careless.

The risk is drift: rename or re-case `OrderStatuses.Delivered` and
`TryReturn` silently starts rejecting every return with
`OrderNotDelivered`.

**Honest assessment of how exposed this actually is:** the existing tests
would catch it, incidentally. `ReturnOrderHandlerTests.cs:28` and
`ConcurrentReturnTests.cs:66` set the order's status *from*
`OrderStatuses.Delivered`, so a rename would make the setup value and the
domain's literal disagree and the tests would fail. The coupling is
therefore already load-bearing — it is just implicit, resting on those
tests happening to use the constant.

**Fix.** Make it explicit, using the pattern this repo already established
for exactly this situation. `Orders.Worker/CustomerTierThresholds` mirrors
`Orders.Domain.CustomerTiers` across an intentional assembly boundary and
says so:

> Mirrors Orders.Domain.CustomerTiers - Orders.Worker deliberately doesn't
> reference Orders.Domain, so these are duplicated **with a test pinning
> the two together**.

Do the same here: a small internal `OrderStatusNames` in `Orders.Domain`
holding the literals with a comment naming `BuildingBlocks.OrderStatuses`
as the source of truth and the csproj constraint as the reason, plus a
one-assert pinning test in `Orders.UnitTests` (which references both).
Turns an incidental coupling into a stated one.

---

## Implementation plan

### Phase 1 — Close the loyalty loophole (1 session)

Finding 1. Highest value: it is money, it is reachable today, and the
domain method is already written and tested.

1. `ReverseCompletedOrderAsync` on `Orders.Worker/CustomerTierStore.cs`,
   mirroring `RecordSql` with `GREATEST(..., 0)` flooring and no downward
   tier re-derivation.
2. Wire it into `OrderStatusStore.ApplySideEffectsAsync` on `Cancelled`,
   gated on the pre-write status having been `Confirmed` or later.
3. Mirror both into `EfOrderStatusRepository` (operator path).
4. Wire it into `EfOrderReturnRepository.SaveReturnAsync` under
   `markOrderReturned`.
5. Document the partial-return decision explicitly in `Customer`'s doc
   comment.

**Done when:** an integration test confirms an order, cancels it, and
asserts `lifetime_spend` is back where it started — and a second test does
the same through a full return.

### Phase 2 — Decide and document the backorder queue policy (1 session)

Finding 2.

1. Pick the policy (recommend: skip-with-starvation-guard).
2. Implement it in `ReleaseBackordersAsync`, with the comment the line
   currently lacks.
3. Test in `BackorderTimeoutSweeperTests`: quantities 10 then 1, restock 3,
   assert the fillable one is filled.
4. Re-check the timeout interaction — whichever policy is chosen, an order
   that could have been filled should not be cancelled by the sweeper.

**Done when:** a partial restock fills the backorders it can cover, and the
chosen fairness policy is stated in the file.

### Phase 3 — Bound the risk query (1 session)

Finding 3. Apply the provider-branch pattern already present 30 lines below
the problem, then confirm `Payments.UnitTests` still passes on SQLite.

**Done when:** no query in the payment-decision path scales with a
customer's order count.

### Phase 4 — Pricing and domain hygiene (1 session)

Findings 4 and 5, both small.

1. `DiscountPriority` ordering inside `CapDiscounts`, plus the first
   `PricingEngineTests` case that configures a >100% stack.
2. `OrderStatusNames` in `Orders.Domain` with a pinning test against
   `BuildingBlocks.OrderStatuses`.

---

## What is genuinely solid

I went looking for arithmetic bugs in the money path and did not find any.
Stating what held up, and why, is more useful than a longer findings list:

- **`MoneyAllocation` is correct, and for the stated reason.** Allocating
  by cumulative floor division over integer minor units makes each share
  the difference between two points on a non-decreasing curve — so no share
  can be negative and the last cumulative equals the total exactly. The doc
  comment's claim about NodaMoney's `Split` handing back negative shares is
  the kind of thing most codebases would have hit in production and never
  diagnosed.
- **The tax-allocation invariant actually holds.** `AllocateTax`'s comment
  asserts a line's discounted value can never be negative "because
  LineDiscounts is itself an allocation of an order-level discount already
  capped at the subtotal". I checked the chain: the cap is real
  (`NRulesPricingEngine.Price`), and `Allocate` distributes proportionally
  to line subtotal, so each line's share is bounded by its own subtotal.
  The comment is load-bearing and true.
- **A full return refunds exactly the order's grand total.** `OrderLine.LineTotal`
  is the *post-discount* goods value, `LineTaxes` are stored per line at
  checkout, and `ShippingRefundPolicy` adds shipping only on a complete
  return. Summed: `(subtotal − discountTotal) + taxTotal + shippingTotal` =
  `grandTotal`. Storing line tax at checkout rather than re-deriving it
  from a rate that may have moved is what makes this stay true over time.
- **`Payment`'s state machine cannot double-charge or over-refund.**
  `TryCapture`/`TryRefund`/`TrySettleWithoutCapture` all guard on current
  state and return `false` rather than throwing, so a redelivered command
  is a no-op. `TryRefund`'s cumulative `RefundableAmount` guard means a
  cancellation landing on a payment a return already partially refunded
  gives back only what remains — and `TryCancel`'s doc comment reasons
  through all six terminal states explicitly.
- **Coupon reservation counts reservations, not completions**, which is the
  only version that survives concurrent checkout, and the release path was
  already widened to `Confirmed` as well as `Reserved` after fulfilment
  states made confirm-then-cancel reachable.
- **`TryReturn` guards on `Delivered`, per-line `ReturnableQuantity`, and
  positive quantities** before computing anything, so the refund arithmetic
  never runs on a request that should not have been accepted.
