# Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk

## Why

Sixty-five milestones of distributed-systems machinery had been built on top of a domain that barely existed. The evidence was in the code's own comments:

- `Order` was `{customerId, amount, currency, status}`. No line items, since Milestone 7.
- `SagaSkuMapper` picked a SKU by hashing the order id (`hash(orderId) % 9`). Its own XML doc called this "clearly a simplification and not a real line-item lookup" - meaning every inventory reservation in every prior saga demonstration held stock for a product nobody had ordered.
- There was no checkout. `Cart.Service` (Redis) and `Orders.Api` (Postgres) were disconnected; nothing turned a cart into an order. `CartLineItem`'s comment promised "checkout is where prices get revalidated" - checkout was never built.
- The payment decision was `amount > 1000 → decline`, a deterministic stub.
- No pricing logic at all: no discounts, coupons, shipping, or tax.

The saga, outbox, inbox, schema registry and compensation work were all real. What they were operating *on* was not. This milestone gives them something real to operate on, and uses four tools that only earn their place once the domain is rich enough to need them.

## What changed

### Line items and checkout

`Order` gained `OrderLine` (SKU, product name, category, quantity, unit price, per-line subtotal/discount/total) plus a pricing breakdown (`Subtotal`, `DiscountTotal`, `ShippingTotal`, `TaxTotal`, `CouponCode`). `Amount` keeps its name and meaning - the grand total - so the Avro contract, read-model projection, event store and cached order needed no lockstep change; only how it is *derived* changed.

`POST /orders` now accepts `{customerId, items: [{sku, quantity}], couponCode?}`. Note what the request does **not** carry: a price. The client states what it wants to buy, and `OrderPricingService` reads the current price from `Catalog.Service` server-side. A tampered request cannot buy a notebook for one centavo. This is also where the cart's deliberately-stale snapshot price (the price the shopper saw when they added the item) gets revalidated against reality - exactly as `CartLineItem` had promised.

`SagaSkuMapper` is deleted. The orchestrated saga now reserves the SKU the customer actually ordered.

### Expand/contract, not a breaking change

The amount-only shape (`{customerId, amount, currency}`) still works. The k6 load scripts, smoke tests, Pact contracts and the README quickstart all post it, and breaking them all simultaneously would have made a pricing regression indistinguishable from a migration mistake. Both shapes run side by side; `items` wins when present. An amount-only order has no lines and reports `pricing: null` rather than a breakdown full of zeroes - "no pricing detail" and "nothing was discounted" are different statements.

### Schema evolution, done properly

`OrderCreated` is Avro-serialized through the Schema Registry, so adding lines is a schema change. The new `lines` array carries a default of `[]`, making it backward-compatible, and `SchemaVersion` moved to 2.

The three consumers that rejected `SchemaVersion != 1` were the real hazard: pinning to one exact version turns a *compatible* schema change into a rolling-deploy outage, because during the rollout both versions are genuinely on the topic. They now accept any version they can read (`OrderCreatedSchemaVersions.IsSupported`).

`OrderCreatedSchemaEvolutionTests` proves both directions by round-tripping through real Avro binary encoding with deliberately mismatched reader/writer schemas - a v1 producer's message read by a v2 consumer (lines materialise from the default) and a v2 producer's message read by a v1 consumer (the unknown field is dropped). The registry's own compatibility check covers the schemas; only this covers whether *this codebase's* reader survives the mixed-version window.

### Scored payment risk

`PaymentDecisionOptions.DeclineAmountThreshold` is gone, replaced by `PaymentRiskEvaluator`: `HIGH_VALUE` (50), `FIRST_PURCHASE` (20), `VELOCITY` (35, three or more payments in five minutes), `ATYPICAL_AMOUNT` (30, more than 5x this customer's own approved average). Declined at 60 or above.

Scored rather than boolean because signals compound: a first purchase is unremarkable, and a large first purchase from an account placing its third order in five minutes is not. Neither `HIGH_VALUE` nor `FIRST_PURCHASE` declines alone; together they do.

This also makes the payment step genuinely **stateful** for the first time. The old rule was a pure function of one number, which meant no amount of saga machinery around it was exercising a decision that could depend on anything else. Now a replayed message that re-ran the evaluation would see its own earlier write in the history - so the inbox deduplication on both saga paths matters for a correctness reason, not just tidiness.

`Payment` gained `CustomerId` (with a covering index on `(customer_id, decided_at)`, on the hot path rather than a reporting convenience). For the orchestrated path, `CustomerId` had to be threaded through `SagaOrchestrationState` → `PaymentDecisionRequested`, since the decision request is issued at step 2 from the saga row rather than from the original event. Keeping both saga paths equally capable is what Milestone 65 established, and letting only choreography gain real decision logic would have quietly undone it.

## The four tools, and why each earned its place

### NodaMoney - because the centavo problem is real

Allocating an order-level discount across lines proportionally, then rounding each share, silently loses money: three lines sharing R$10,00 each get R$3,33 and the order's line totals no longer sum to what was charged. `Money.Split` distributes the remainder instead (R$3,33 / R$3,33 / R$3,34).

Verified over 200,000 random allocations with zero drift before being adopted, and pinned by a property test since. `Money` is the only dependency `Orders.Domain` has - a pure computation library, so the domain-purity fitness functions still hold. It is used for *computation*; the persisted and wire representations stay `decimal` + currency code, which is what kept the blast radius sane.

### NRules - because promotions compose and `if`/`else` does not

Four promotion rules (`CouponPercentageRule`, `CategoryDiscountRule`, `BulkQuantityRule`, `FreeShippingRule`), each authored independently, each unaware of the others, combining automatically. `CategoryDiscountRule` uses NRules' `Collect` aggregate to gather every line in a discounted category - one declarative clause where hand-written code grows a nested group-by.

The split is deliberate: promotions are rules; shipping and tax are arithmetic policy applied in a fixed order (tax on the *discounted* subtotal, never the gross one), so encoding their ordering as rule priorities would add fragility to buy nothing. Free shipping is the one genuine promotion among them, and it *is* a rule - it grants a marker fact the engine reads.

The engine owns the one invariant no individual rule can enforce: **discounts are capped at the subtotal**. Two independently-authored campaigns each granting 60% is entirely plausible, and without the cap the order total goes negative.

### CsCheck - because the dangerous cases are the ones nobody writes down

Every other test in this repository asserts on hand-picked examples. Pricing is the first logic here whose interesting failures live in combinations nobody thinks of. Ten properties, 10,000 generated orders each:

- the grand total is never negative
- discounts never exceed the subtotal
- per-line discount shares sum to *exactly* the order discount
- itemised discounts sum to the discount total (including after the cap trims one)
- the grand total always equals its parts
- pricing is deterministic (rule engines evaluate in a non-obvious order; this pins that the *result* does not depend on it)
- presenting a valid coupon never costs the shopper more

That last one is monotonicity, and it is the property that would catch free shipping being computed on the discounted subtotal - where a coupon could drop an order below the threshold and silently *add* R$19,90 of shipping.

**These properties were confirmed to have teeth.** Temporarily swapping `Money.Split` for naive proportional rounding made `PerLineDiscountsSumToExactlyTheOrderDiscount` fail, and CsCheck shrank the counterexample to a minimal failing order in 6 shrinks with a reproducible seed. A property test that has never been seen to fail is not evidence of anything.

### FluentValidation - because the request grew a conditional shape

`CreateOrderCommandValidator` built a `Dictionary<string, string[]>` by hand. With two request shapes (rules that apply only to one), per-item rules needing indexed keys like `Items[2].Quantity`, duplicate-SKU detection and a coupon field, it had started reimplementing what FluentValidation does. The static `Validate`/`Normalize` surface was kept so no caller outside that file changed.

## Verification

### Local

131 tests pass (up from 99): 32 new across pricing examples, pricing properties, schema evolution and risk rules. Two new architecture fitness functions pin the boundary - `Orders.Domain` must not depend on NRules or FluentValidation, and (guarding that rule from passing trivially) the pricing model must still actually live in `Orders.Domain.Pricing`.

### Against the real stack

Validated on the lab server's Docker Compose stack against live Postgres, Kafka, Schema Registry, Keycloak and MongoDB.

**Legacy shape still works:** `{customerId, amount: 49.90, currency}` → `201`, `pricing: null`.

**Line-item checkout with stacked promotions** - 2x `SKU-BOOK-001` + 1x `SKU-ELEC-001`, coupon `SAVE10`:

| | |
| --- | --- |
| Subtotal | 2 × 89,90 + 4.299,90 = **4.479,70** |
| `SAVE10` (10%) | 447,97 |
| `CATEGORY-ELECTRONICS` (5% of 4.299,90) | 215,00 |
| Discount total | **662,97** |
| Shipping | 0,00 (subtotal cleared the 200,00 threshold) |
| **Grand total** | **3.816,73** |
| Per-line shares | 26,61 + 636,36 = **662,97 exactly** |

Prices came from the catalog; the request never mentioned them. Two independently-written rules stacked, and the allocation was cent-exact.

**Risk rules are genuinely stateful.** The same order was placed twice for the same customer, and got *different decisions*:

| Order | Signals | Score | Outcome |
| --- | --- | --- | --- |
| First (3.816,73) | `HIGH_VALUE(+50)`, `FIRST_PURCHASE(+20)` | 70 | **Declined** |
| Second (3.816,73) | `HIGH_VALUE(+50)` | 50 | **Approved** |

Identical amount, identical pricing - the only thing that changed was that the customer now had history. The old stub could not have produced this.

**The saga reserves the real product.** In `Orchestration` mode, an order for 1x `SKU-BOOK-002` (74,90) + 2x `SKU-HOME-001` (359,80):

- `SKU-HOME-001`: available 23 → **21** - exactly the two units ordered
- `SKU-BOOK-002`: 100 → 100, untouched
- `reserved_quantity` back to 0 - the commit step converted the hold into a permanent deduction
- Order reached `Confirmed`

**Migration backfill verified:** all 8 pre-existing orders report a `subtotal` consistent with their `amount_cents`. Without the backfill they would have carried `subtotal = 0` alongside a non-zero total - a breakdown contradicting itself, and one `Order.CreateWithLines` could never produce.

## Stated simplifications

- **The saga tracks one line per order.** `SagaOrchestrationState` is one row with one SKU/quantity; widening it into per-line reservations with per-line compensation is a genuinely larger change. The orchestrator takes the largest line by value. This is a simplification, but it is now a *real line from a real order* rather than a hash.
- **Tax defaults to 0%.** The mechanism (applied to the discounted subtotal, itemised as a charge) is implemented and tested; no real tax table is modelled.
- **Multi-currency orders are rejected** rather than converted. Inventing an exchange rate is not this lab's business.

## Cart → checkout, wired end-to-end

`POST /api/storefront/checkout` (Storefront.Service) is the piece that was still missing when this milestone first shipped: it reads the shopper's cart from `Cart.Service`, submits it to `Orders.Api` as line items with the injected Keycloak bearer token, and clears the cart - all server-side, and in that order.

The ordering is the one invariant worth being explicit about. The cart is cleared **only after** Orders.Api has genuinely accepted the order:

- A rejected order (bad SKU, catalog unavailable, validation failure) leaves the cart untouched, so the shopper can fix the request and retry without re-adding every item.
- A cart-clear failure **after** a successful order is logged, not surfaced as a checkout failure - the order is real and already accepted by that point, and reporting an error would tell the shopper they were not charged for something that already exists.

`CheckoutEndpointTests` exercises both directions directly (no HTTP host needed - `CheckoutAsync` takes its collaborators as parameters), including the case that matters most: cart-clear throwing after a successful order still reports success to the caller.

The demo storefront (`wwwroot/`) now posts `{cartId, customerId, couponCode?}` to this endpoint instead of the legacy `{customerId, amount: cart.total, currency}` shape - and never states a price. A coupon field was added to the cart panel (`SAVE10`, `SAVE20`, `HALFOFF` are seeded) purely so the pricing engine has something to demonstrate from the UI, not just from `curl`.
