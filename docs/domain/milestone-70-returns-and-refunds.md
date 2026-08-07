# Milestone 70: Returns, Partial Refunds, and a Money Bug in Shipped Code

## Why this is where the per-line discount pays off

Milestone 66 stored a `LineDiscount` on every order line — each line's prorated share of the order's discounts — with a comment saying it was kept so partial refunds would not have to re-derive an allocation that might no longer reproduce. This is that milestone.

A shopper buys 3 shirts at R$219,90 with a 10% coupon: subtotal R$659,70, discount R$65,97, **charged R$593,73**. They send one back. Refunding it at list price hands back R$219,90 for a unit that cost R$197,91 — a 11% loss on every returned item, invisible until someone reconciles the ledger. The refund has to come out of `LineTotal`, and that number exists only because it was computed at checkout and stored, rather than recomputed now from promotion rules that may have ended, changed, or run out of redemptions since.

## The invariant that shapes the arithmetic

Returning a line one unit at a time must refund exactly what returning it all at once would — no more, no less, no matter how the customer splits their parcels.

The refund is therefore the **difference between two points on a cumulative curve**, not a sum of independently-rounded per-unit prices:

```
refund(alreadyReturned, n) = cumulative(alreadyReturned + n) − cumulative(alreadyReturned)
cumulative(k)              = floor(lineTotalInCents × k ÷ lineQuantity)
```

Cumulative is monotonically non-decreasing, so every increment is non-negative; and `cumulative(quantity)` is the line total exactly, so the parts always reconstitute the whole.

## The bug this found in already-shipped code

Milestone 66 adopted NodaMoney's `Split` for the per-line discount allocation on the strength of a measurement: 200,000 random allocations, zero drift. That measurement checked the wrong thing. It confirmed the shares always **sum back** to the original — and they do. It never checked whether an individual share could be **negative**.

```
Money(0.06, BRL).Split(11)
  →  0.01 ×10,  then  -0.04        // sums to 0.06; the last share is negative
```

Measured across 200k random inputs:

| | negative share produced |
| --- | --- |
| `Split(int n)` — equal split | ~1 in 1,000 |
| `Split(int[] weights)` — weighted | ~1 in 200,000 |

Both are wrong in the direction that costs money. A negative **discount** share is a line whose discount raises its price. A set of **refund** shares containing a negative one lets a partial return refund more than the line was charged — returning the first ten units of that 0.06 line would refund 0.10.

`Split(1)` also throws outright, demanding two or more shares despite the message claiming one — and a line of a single unit is the most common line there is.

Both paths now use `MoneyAllocation`, which keeps the exact-sum guarantee and adds non-negativity by construction. And the pricing property tests gained the check that would have caught this the first time:

```csharp
public void NoLineEverReceivesANegativeDiscountShare()
public void NoLineIsEverDiscountedBelowFree()
```

**This is the honest lesson of the milestone.** The Milestone 66 measurement was real, large, and reassuring, and it validated a property that was true while missing one that was not. A property test is only as good as the properties someone thought to write down — and "the parts sum to the whole" is a weaker statement than "the parts sum to the whole *and none of them is absurd*". The weighted case is rare enough (1 in 200k) that the 10,000-iteration property test may not reliably reproduce it; the direct 200k measurement is the stronger evidence, and it is what is recorded here.

## The flow

`POST /orders/{id}/returns` with `{ items: [{ sku, quantity }], reason }`.

- Only a **Delivered** order can be returned. Nothing that never arrived can come back, and a cancelled order was never charged.
- The order, the updated per-line returned quantities, the refund command and one restock command per SKU are all written **in one transaction**. A refund command that outlived a rolled-back return would give money away for goods the shopper still has.
- Restock commands are keyed by **SKU**, not order id — Inventory serialises stock changes by partition key (Milestone 41), so a restock keyed otherwise could land on a different partition than the reservation it reverses.
- When every line has come back, the order moves to **`Returned`**. `Delivered` consequently stopped being terminal: it is the happy ending, but not the end of the row's life.

Two new domain guards, both cumulative rather than boolean, because returns are partial by nature:

- `Payment.TryRefund` — only a captured payment can be refunded (money never taken cannot be returned), and never more than `Amount − RefundedAmount`. A payment becomes `Refunded` only once fully refunded; a partial refund leaves it `Captured`.
- `InventoryItem.Restock` — distinct from `TryRelease`, which hands back stock that was only ever *held*. A return is the opposite: the sale happened and the stock left inventory entirely, so there is no reserved quantity to draw down. A pure increment that cannot fail; the inbox is what stops a redelivered restock inflating stock twice.

## Verification

### Local

189 tests pass (up from 180), including two property-based checks on the refund arithmetic:

- no sequence of partial returns, in any grouping, may refund more than the line was charged — and returning everything refunds exactly it;
- every refund share is a whole centavo and never negative.

The first of these is what found the `Split` defects, shrinking straight to `quantity = 1` and then to the negative-share case.

### Against the real stack

3 × `SKU-CLTH-002` (R$219,90) with `SAVE10`, paid by Pix, walked through to Delivered:

| Step | Refund | Stock | Payment | Order |
| --- | --- | --- | --- | --- |
| charged | — | 40 → 37 | `Captured` R$593,73 | `Delivered` |
| return 1 of 3 | **R$197,91** | 41 | `Captured`, refunded 197,91 | `Delivered` |
| return remaining 2 | **R$395,82** | 43 | **`Refunded`**, refunded 593,73 | **`Returned`** |
| return more | — | — | unchanged | `409` |

`197,91 + 395,82 = 593,73` — exactly what was charged. And R$197,91 is the *discounted* unit price; refunding at list would have returned R$219,90 and lost R$21,99 per shirt.

## Stated simplifications

- **Shipping and tax are not refunded.** Only line totals come back. Most storefronts refund shipping only on a full return, which needs a rule this lab does not model.
- **No return window.** A delivered order can be returned indefinitely.
- **No approval step.** A return is accepted and refunded immediately; real operations inspect the goods first, which would add a `Requested → Received → Refunded` lifecycle of its own.
- **The refund is not itself a saga.** The refund and restock commands are queued transactionally and delivered at-least-once, but nothing compensates if Payments accepts the refund and Inventory never restocks — the discrepancy is recoverable and visible, not silently lost.
