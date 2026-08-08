# Milestone 82: A Refund Is the Whole Charge, Not Just the Line

## Scope

`Order.TryReturn` (Milestone 70) refunds `LineTotal` - a line's discounted goods price - for every unit that comes back. Two things an order actually charges were never part of that number:

1. **Tax on the returned units.** `Order.TaxTotal` is charged and never refunded on any return, complete or partial. A fully-returned order keeps 100% of the tax it collected on goods the shopper sent back.
2. **Outbound shipping**, on a return that empties the order entirely. Under Brazil's Código de Defesa do Consumidor art. 49 - the *direito de arrependimento*, a no-reason-required cancellation right inside seven days of receipt - the refund owed is the full amount, shipping included. A defective-item return owes the same, no window. This lab charged shipping once and never gave it back under any circumstance.

Both are the same class of bug M70 itself opened with: refunding less than was charged.

## Design

**Tax proration mirrors discount proration, weighted by discounted value.** `PricingAllocation.AllocateTax` is a new sibling to `AllocateDiscounts` (Milestone 66), reusing the same `MoneyAllocation.Allocate` cumulative-floor-division primitive - exact, never negative, sums to exactly `TaxTotal`. It weights by each line's *discounted* value (`LineSubtotal - LineDiscount`), not raw subtotal, since `NRulesPricingEngine` computes tax on the discounted subtotal - a heavily-discounted line should carry proportionally less of it. In practice this weighting is numerically close to plain subtotal-weighting for anything the real pricing engine can produce, because `AllocateDiscounts` itself always spreads a discount proportional to raw subtotal regardless of which rule granted it (a category promotion that only matched one line still shows up as an even split across equal-subtotal lines) - so discounted value ends up proportional to subtotal too, for every line, by construction. The distinction is real and matters when a future promotion breaks that proportionality (a fixed-amount-off-one-line rule, say); it is exercised directly with hand-picked inputs in `PricingAllocationTests`, since the current rule set can't produce a case where it visibly diverges from subtotal-weighting.

`OrderLine` gains `LineTax`, stored at checkout alongside `LineDiscount` - same reasoning: a return refunds what a line actually paid, not a rate re-derived from config that may have changed since. `Order.TryReturn` now computes two refund shares per returned line - `ReturnRefundCalculator.RefundForUnits` against `LineTotal` (unchanged) and, new, the identical call against `LineTax` - and sums them. The calculator itself needed no change; it was already a general "cumulative share of N of M units of this total" function, indifferent to what the total represents.

**Shipping refund is a policy decision, pulled out as its own pure function.** `ShippingRefundPolicy.IsOwed(orderFullyReturned, reasonCategory, orderCreatedAt, requestedAt, regretWindow)` owes shipping only when the return empties the order (a partial return leaves the parcel it shipped in still partly fulfilled) and the reason category calls for it:

```
Defect    → owed, unconditionally, on a complete return
Regret    → owed only inside regretWindow of the order's CreatedAt
Unwanted  → never
```

`ReturnReasonCategory` is a real enum, not a bare string like `OrderStatuses` or `PaymentStates` - unlike those, nothing outside Orders' own EF-mapped boundary needs to read it (no other service talks to `order_returns` over raw SQL), so the usual reason for those to be string constants doesn't apply here, and a checked enum plus `HasConversion<string>()` is the more idiomatic EF choice for a single-service concern.

**Extracted, not inlined, for a concrete reason: nothing in `Order`'s public API can reach `Delivered`.** Status transitions live outside the aggregate entirely - in `Orders.Worker`'s `OrderStatusStore` and `Orders.Infrastructure`'s `EfOrderStatusRepository`, both driving the row via raw SQL compare-and-set, never through a domain method (see Milestone 81's audit of that same boundary). `Order.TryReturn` itself is therefore not something a unit test can exercise end to end without a live database moving the order through its fulfilment states first. Pulling the shipping-refund decision out as a standalone static function - the same reasoning that already made `ReturnRefundCalculator` its own public class rather than inline arithmetic - gives it somewhere to actually be tested.

**The regret window is the one number that comes from outside.** `ReturnOptions.RegretWindowDays` (default 7, matching CDC art. 49) is ordinary configuration, bound the same way `PricingOptions` is; everything else - whether *this* return, on *this* order, actually falls inside it - is computed from facts the aggregate already holds (`CreatedAt`, the request's `requestedAt`), the same "policy is code, the numbers behind it are data" split `PricingOptions` already draws for promotions.

**The API defaults to the safest category, not the most generous one.** `CreateReturnRequest.ReasonCategory` is optional; absent or unrecognized, it falls back to `Unwanted` - the one category that never owes shipping. An old client, or a client that never learns about this milestone, keeps getting exactly the refund it got before: goods and (new) tax, never a shipping refund it didn't ask for. Existing rows backfilled by the migration get the same default, for the same reason - they cannot retroactively prove they'd have qualified as `Defect` or an in-window `Regret`.

## A migration note

`order_returns` gains `reason_category` (non-null, defaulted to `'Unwanted'` for existing rows - never `''`, which matches no enum member) and `shipping_refund`; `order_lines` gains `line_tax`, defaulted to `0` for existing rows. Both are ordinary additive, backward-compatible column adds - no expand/contract dance like Milestone 20's, since nothing reads these columns before this milestone's code does.

## Verification performed

Same environment constraint as Milestone 81: no Docker here, so nothing below reaches a real Postgres or a live cluster.

- **Full solution build**: 0 warnings, 0 errors.
- **The EF migration was generated by `dotnet ef migrations add`** against `OrdersDbContextFactory`'s design-time factory (no live database needed to scaffold), not hand-written - the generated `Up`/`Down` were reviewed and the `reason_category` backfill default corrected from EF's own blank-string default to `'Unwanted'`.
- **`Orders.UnitTests`**: 190/190 passing, 25 new across three areas:
  - `PricingAllocationTests` (4 facts) - `AllocateTax` in isolation, including a hand-constructed case where two equal-subtotal lines carry unequal discounts, which is the only way to actually observe discounted-value weighting diverge from subtotal weighting (see Design).
  - `PricingEngineTests` (3 new facts) - tax proration through the real engine, including the equal-split-under-a-targeted-promotion case that documents *why* `PricingAllocationTests` had to test the weighting directly instead.
  - `ReturnRefundTests`/`ShippingRefundPolicyTests` (8 new facts) - a line's goods-plus-tax refund arithmetic, and every branch of the shipping policy: partial return (never owed), complete Defect (always owed), complete Regret inside/exactly-at/past the window, complete Unwanted (never owed).
- **`Orders.ArchitectureTests`** (85/85, up from 81 - the new types were picked up by the existing fitness functions, not exempted from them) and **`Services.ArchitectureTests`** (80/80) both pass unchanged.
- **Not verified in this pass**: the migration applied to a real database, the `/orders/{id}/returns` endpoint's new `reasonCategory` field end to end, and Pact consumer contracts re-verified against a live provider (the field is additive and optional, so no existing interaction should break, but this was not confirmed against a running Pact broker).

## What was deliberately not done

- **Exchanges**, as distinct from a return-then-new-order. Still out of scope, same as M70 left it.
- **An RMA authorization step** before a return is accepted - this lab's return, like M70's, is self-service and immediate.
- **A live-cluster demonstration of the regret window actually expiring** - the property is proven at the unit level (`ShippingRefundPolicyTests`), not watched happen against a real clock on a real order, the way most of this repository's other milestones close.

## See also

- [Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk](milestone-66-line-items-pricing-and-risk.md) — `AllocateDiscounts`, `MoneyAllocation`, and the per-line discount storage this milestone's tax proration extends.
- [Milestone 70: Returns, Partial Refunds, and a Money Bug in Shipped Code](milestone-70-returns-and-refunds.md) — `ReturnRefundCalculator` and the "refund what was charged, not list price" principle this milestone applies to the two components it left out.
- [Milestone 71: The Customer Stops Being a String](milestone-71-customers-tiers-and-geography.md) — the destination-based shipping and regional tax this milestone's refund policy now accounts for on the way back.
- [Milestone 81: Cancelling an Order Gives Back Everything It Took](milestone-81-cancellation-compensation.md) — the sibling gap on the cancellation path, and the audit that surfaced this one alongside it.
