# Milestone 72: Stock Lives in Buildings

## The question that stopped having one answer

`inventory_items` held one row per SKU with an available count. That row answers "do we have 3 of these?" — and for a shop with one warehouse, that is also the answer to "can we ship 3 of these." The moment there is more than one building, the two questions come apart: 3 units held as 2-in-São-Paulo and 1-in-Rio is a fillable order, but not from anywhere in particular, and something has to decide.

This milestone splits stock into one row per `(SKU, warehouse)` and puts a real allocation policy in front of it.

## The policy is a pure function

`StockAllocator.Allocate(candidates, quantity)` takes availability and returns a plan. No database, no I/O, no clock. That is what makes the interesting part — the policy — property-testable at all; a policy that can only be exercised against a live Postgres is a policy nobody property-tests.

**Fewest warehouses first.** Prefer a single warehouse that can cover the whole order; split only when none can. Splitting is not free — two parcels, two shipping costs, two chances for one leg to go missing — so it is a fallback, not the default. Ties break on the warehouse's configured priority, then on its code, so the plan never depends on the order rows came back in.

**All or nothing.** If the network cannot cover the request, the plan is unfulfillable rather than partial. The saga's reservation step confirms an order, and a partial reservation would confirm one the warehouse cannot actually fill.

### The properties that matter

Two of the ten tests are generated rather than hand-picked, because the failures worth finding here live in combinations nobody writes down:

- **Never over-allocate, always sum to the request.** No warehouse is asked for more than it holds, no warehouse appears twice, and the plan covers exactly what was requested — never less (a short shipment) and never more (stock conjured from nowhere). Refusal is legitimate only when the network genuinely cannot cover the request.
- **Never split when one warehouse could have covered it.** The property that keeps the "fewest warehouses" promise honest across every stock configuration, not just the three in the example tests.

One example test carries a note about its own history. `OneWarehouseThatCanCoverTheOrderShipsItWhole` originally asserted `WH-SP` because `WH-SP` was listed first in the input — and the allocator correctly returned `WH-RJ`, because the two tie on priority and `WH-RJ` sorts first. The test was wrong, not the code, and it was wrong in exactly the way the determinism property forbids: it depended on input order.

## Reserve records what it drew

A reservation spanning two warehouses can only be released by knowing which buildings it came from. Guessing moves stock between warehouses on paper — the kind of discrepancy nobody notices until a stocktake — so `reservation_allocations` stores the plan, and commit and release replay it rather than re-deriving it.

No row locking, and none needed: Inventory consumes reservation commands partitioned by SKU (Milestone 41), so two requests for the same SKU are never processed concurrently. That is the same guarantee `InventoryItem` has relied on since it was written, now spanning several rows instead of one.

## Two bugs I put in and had to take out

The first version of this wiring was wrong twice, and both only showed up on the lab server.

### The seed invented stock

The migration seeded `WH-SP` with the full existing `available_quantity` and `WH-RJ` with a third more:

```sql
SELECT sku, 'WH-SP', available_quantity, ...   -- all of it
SELECT sku, 'WH-RJ', available_quantity / 3, ...  -- and a third again
```

That is 133% of the stock the shop actually had. `inventory_items` and the warehouse network now disagreed about how much existed, and every order sat between two sources of truth. The fix is to **split** rather than duplicate — `WH-RJ` takes a third, `WH-SP` keeps the remainder — so the network sums to exactly what the aggregate already claimed. Verified with a query that returns the SKUs where `inventory_items.available_quantity <> SUM(warehouse_stock.available_quantity)`; it returns nothing.

### `&&` leaked reservations

The decision was written as a chain:

```csharp
var reserved = plan.Fulfillable
    && await TryApplyReservationAsync(...)   // draws stock down
    && item.TryReserve(quantity, processedAt); // can still say no
```

It reads as one decision and is not. With the inflated seed, an order for 25 units found 25 in the network, drew them down — and *then* the aggregate row refused, because it only knew about 21. The reply said "not reserved," the saga cancelled the order, and nothing ever released the hold, because nothing releases a reservation that never succeeded. Both warehouses sat holding stock for an order that had been told no.

Observed on the server before the fix:

```
WH-SP  available 0   reserved 21     order status: Cancelled
WH-RJ  available 3   reserved  4     allocations still on file: 2
```

25 units gone, quietly, with no error anywhere. Fixing the seed alone would have hidden this — the two views would agree, so the second guard would stop refusing — which is why the ordering is fixed too. **Decide first, mutate second:** ask both whether they can, and only then let either move.

## Validated end to end

With the corrected seed, on the lab server, `SKU-BOOK-001` at 3 units in São Paulo and 3 in Rio — an order for 5 cannot come from one building:

| | WH-SP | WH-RJ |
| --- | --- | --- |
| before | 3 available | 3 available |
| reserved | 3 drawn | 2 drawn |
| after commit | 0 available, 0 held | 1 available, 0 held |

Allocations cleared, aggregate and network still in agreement.

The compensating path was validated by accident and is the better test for it: an 80-unit order for R$6.616,64 reserved across both warehouses (66 + 14) and was then **declined by Milestone 66's payment risk rules**. The release replayed the recorded plan and both warehouses came back to exactly where they started. A split reservation unwound correctly under a failure nobody staged.

One more result worth keeping: an order for 9 units of a SKU holding 4 network-wide was refused rather than partially allocated, and the saga cancelled the order. All-or-nothing, working.

## Reservations that predate this milestone

`TrySettleReservationAsync` returns `false` when no plan is on file, and the caller falls back to the single-warehouse path. Orders in flight during the rollout still settle — they just settle against the aggregate, as they always did.

## What this is not

- **Priority is a hardcoded rank**, not distance. A real network ranks warehouses by proximity to the destination, which needs the shipping address to reach this service — it currently does not.
- **`ReorderPoint` is reporting only.** Nothing acts on `NeedsReplenishment`; it is the number a replenishment job would read. Leaving it out entirely would have made the model quietly claim stock levels need no management.
- **Restock picks the emptiest warehouse**, a stand-in for "wherever the returns depot routes them." A real network decides that from the return label.
- **The split path only runs under orchestration.** Choreography never routes through Inventory, so `Saga__Mode=Orchestration` is required to exercise any of this.

## See also

- [Milestone 41: Inventory Service with Kafka-Partitioned Stock Reservation](../architecture/milestone-41-inventory-kafka-partitioning.md) — the guarantee that makes lock-free allocation correct.
- [Milestone 69: The Order's Life Does Not End at Confirmed](milestone-69-order-lifecycle.md) — the state machine that returned the 409 when this milestone's testing tried to skip `Picking`.
