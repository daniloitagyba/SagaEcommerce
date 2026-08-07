# Milestone 71: The Customer Stops Being a String

## What was wrong with `customerId`

Until this milestone a customer was a `varchar` on the orders table. Every question worth asking about a shopper — is this their first order, have they spent enough to earn a discount, where do they live — had no place to be answered from. Pricing therefore treated a first-time buyer and a shopper on their fortieth order identically, and shipping cost the same to São Paulo and to Belém.

Milestone 66 had already built the machinery: `PricingCustomer` and `PricingDestination` were in the pricing request, and the payment risk rules scored on customer history. Both were being fed placeholders. This milestone gives them something real to read.

## Loyalty tiers are derived, never set

`Customer` holds `LifetimeSpend`, `CompletedOrderCount` and a `Tier`. The tier is not a field anybody assigns — it is recomputed from lifetime spend every time spend changes:

```
>= 5000  →  Gold    (7% off)
>= 1000  →  Silver  (3% off)
otherwise → Bronze  (0%)
```

Making the tier derived rather than stored-and-updated means it cannot drift from the number that justifies it. There is no code path that can leave a Gold customer with R$40 of lifetime spend, because there is no code path that writes `Tier` on its own.

### Spend counts on completion, not on order

`RecordCompletedOrder` runs when an order reaches a state where the money is genuinely the shop's — not at checkout. Otherwise the cheapest route to Gold is to place forty orders and cancel all of them.

### Reversal is asymmetric on purpose

`ReverseCompletedOrder` subtracts the spend and decrements the count, but **does not demote the tier**:

```csharp
public void ReverseCompletedOrder(decimal amount)
{
    LifetimeSpend = Math.Max(0m, LifetimeSpend - amount);
    CompletedOrderCount = Math.Max(0, CompletedOrderCount - 1);
}
```

This is a deliberate asymmetry rather than an oversight. A returned order should take back the spend it contributed — the number has to stay honest — but silently stripping a status the customer has been using is a support ticket, and every loyalty scheme in practice demotes on a schedule (annual review) rather than the instant a parcel comes back. The domain records the fact; the demotion policy is a separate decision that this lab does not make.

## Where the tier is applied

`LoyaltyTierRule` joins the four promotion rules already in the NRules session, which means the tier discount **stacks** with the rest rather than replacing them. It also means it goes through the same cap: total discounts can never exceed the subtotal.

Observed on the lab server — a Silver customer buying one R$349,90 electronics item, no coupon:

```
discount R$28,00 = 3% loyalty (R$10,50) + 5% electronics category (R$17,50)
```

Two independent campaigns, composed by the rules engine, with the shopper supplying no coupon code at all. That composition is the reason a rules engine is here instead of an `if` chain.

## Tier progression, observed

The same customer across two orders on the lab server:

| | lifetime spend | tier |
| --- | --- | --- |
| after order 1 | R$997,20 | Bronze |
| after order 2 | R$1.329,60 | **Silver** |

The threshold is R$1.000 and Bronze survived R$997,20 — three reais short, still Bronze. Worth checking explicitly, because an off-by-one on a `>` versus `>=` in tier code is invisible until a customer sits exactly on the boundary.

## Two writers, one threshold table

The tier is written from two places, and they cannot share code:

- `Orders.Domain/Customer.cs` — the domain rule, used at pricing time.
- `Orders.Worker/CustomerTierStore.cs` — a single SQL `UPDATE` that increments spend and re-derives the tier in the same statement, because the worker does not reference `Orders.Domain`.

Duplicating the thresholds in SQL is a real risk: change the domain to Gold-at-4000 and the worker keeps promoting at 5000, and the disagreement shows up as customers whose tier flips depending on which component last touched them. The mitigation is a unit test that reads both constants and asserts they match — the duplication stays, but it cannot drift silently.

The `UPDATE` re-derives rather than reads-then-writes, so two concurrent completions cannot lose one another's spend.

## Geography

`ShippingAddress` derives a `PostalPrefix` — the first two CEP digits — which is the only part of the address pricing actually uses. Shipping is looked up by prefix, tax rate by region.

Observed on the lab server:

| destination | shipping | tax | on R$74,90 |
| --- | --- | --- | --- |
| São Paulo (01) | R$14,90 | 18% | R$13,48 |
| Pará (66) | R$49,90 | 17% | R$12,73 |
| no address given | R$19,90 | 0% | — |

The third row is the one that matters for the rollout. Orders created before this milestone have no address, and an order with no address prices exactly as it did under Milestone 66: the flat R$19,90 and no tax. Nothing that already existed changed its answer.

## What the customer row is not

`GetOrCreateAsync` inserts with `ON CONFLICT (id) DO NOTHING`, so a customer springs into existence on their first order. There is no registration, no profile, no authentication tied to it — the `customerId` still arrives from the token and is trusted. This is a projection built for pricing decisions, not an identity service, and treating it as one would be a mistake.

## See also

- [Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk](milestone-66-line-items-pricing-and-risk.md) — the pricing request this milestone finally fills in.
- [Milestone 69: The Order's Life Does Not End at Confirmed](milestone-69-order-lifecycle.md) — where `RecordCompletedOrder` is called from.
- [Milestone 70: Returns, Partial Refunds, and a Money Bug in Shipped Code](milestone-70-returns-and-refunds.md) — where the spend is reversed.
