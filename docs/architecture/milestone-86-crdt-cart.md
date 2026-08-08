# Milestone 86: Carts That Merge Instead of Overwrite

## Scope

`CartStore` (Milestone 42) is a Redis hash, one field per SKU, plain last-write-wins: `HSET` overwrites whatever was there. That is fine for the ordinary case - one shopper, one session - and wrong for the case this milestone is about: a shopper with two tabs, or a phone that drops connectivity mid-edit and reconnects later. Two concurrent writes to the same field resolve by whichever request's `HSET` happens to land last at Redis, with no relationship to which one the shopper actually intended to win. The specific, well-known failure mode is the one Amazon's Dynamo paper opens with: a concurrent "add this item" and "remove this item" can resolve either way, and the wrong way is silent - an item a shopper just added disappears, or one they just removed comes back, with nothing in the response to say why.

This is the one milestone in this pass that is genuinely new capability rather than a fix to something already wired up - Cart.Service's existing PUT/DELETE endpoints (Milestone 84's `/carts/me/...`) are untouched and still resolve exactly as before for the ordinary single-session case. What's new sits alongside them.

## Design

**`CartItemCrdt`: an Add-Wins Observed-Remove Set composed with a PN-Counter, one per SKU.** Presence and quantity are different questions with different CRDT answers:

- *Presence* is an OR-Set (Bieniusa et al. - the same construction Riak's `Map` and Akka's `ORSet` use). Every "add or increase" mints a fresh, globally-unique `CartDot(ReplicaId, Counter)` and adds it to a live-dots set. A "remove" doesn't record "this SKU is absent" as a fact - it tombstones every dot *this replica currently observes as live*. That distinction is the whole mechanism: a dot minted by a concurrent add that this replica never saw cannot be in its tombstone set, so it survives the merge untouched. **Add wins** over a concurrent remove - not because removal is broken, but because a remove can only ever act on what it has actually seen.
- *Quantity* is a PN-Counter - per-replica positive and negative contributions, merged by componentwise max (the standard grow-only-counter join, applied to both halves independently). "Set the quantity to 3" is really "increase by 2 from what I last saw," so two concurrent increases from different replicas both count instead of one silently overwriting the other.

The join (`CartItemCrdt.Merge`) is three set/map operations - union the live dots, union the tombstones, subtract - each individually commutative, associative and idempotent, which is what makes the whole function all three without any case-by-case argument. `CartItemCrdtPropertyTests` proves it directly against 2,000 randomly generated reachable states per property, rather than by inspection.

**`CartCrdtState` composes per-SKU CRDTs into a whole cart by merging key-wise** - the standard rule for map-of-CRDT composition (Shapiro et al.'s composable-CRDT framework), which needs no proof of its own once each value type's join is already proven. Product metadata (name, price, currency, added-at) rides alongside as a plain snapshot, first-writer-wins, not itself a CRDT value - there is no meaningful "merge" of two product names, and re-snapshotting on every reinforcing `Increase` would let a later, unrelated write quietly overwrite the price a shopper actually saw when they first added the item.

**Where this actually plugs in: `POST /carts/me/merge`, not the existing PUT/DELETE routes.** Rewriting Cart.Service's whole storage model to be CRDT-shaped end to end was the larger, riskier option and wasn't taken - the existing simple-hash storage already works and is tested (Milestone 84). Instead, a new endpoint accepts a list of operations a client tracked while it couldn't reach the cart (a different tab open at the same time, a device that was offline), replayed against the server's current state: the server synthesizes a single CRDT view of whatever it currently has (one dot per present SKU, tagged as coming from one big `"server"` replica - it was never itself divergent, so this is exact, not an approximation), folds the client's submitted operations into their own `CartCrdtState`, and merges the two via the same `CartItemCrdt.Merge` the property tests prove. The wire format is operations (`Increase` / `Decrease` / `Remove` per SKU), not raw dots - a dot is an implementation detail of *this* merge, not something a client should have to construct or a wire contract should have to expose.

## A limitation this design has, named rather than hidden

The simplified wire protocol has no way for a client to carry a *previously-synced* dot forward across sessions - `Remove` in a merge request only reliably takes effect for a SKU the same request's own `Increase` established a dot for. A client removing a SKU it saw during an *earlier* sync, then going offline before submitting the remove, cannot correctly express "the dot I observed then, tombstone it" through this endpoint - the operation folds against an empty local state and finds nothing to tombstone, per OR-Set semantics: a remove can only act on what has actually been observed, and this wire format never gave the client anything to observe. A production version would need the client to carry dot identifiers forward from its last sync, not just SKU/quantity numbers. This lab's endpoint prioritizes a wire contract simple enough to actually implement a client against over completeness on this one case.

**A related, more subtle property, also named rather than hidden**: the PN-Counter's positive contributions are never un-counted by a `Remove` - only presence (the live-dot set) is affected. A SKU removed and later reinforced by a fresh, unrelated `Increase` resurfaces carrying the *sum* of every increase it has ever received across that whole history, not just the new one (`CartItemCrdtPropertyTests.AConcurrentAddSurvivesARemoveOfAnEarlierVersionOfTheSameItem` pins the exact number). This is a known, accepted shape of this CRDT family, not a bug this milestone introduced - a "cleaner" reset-on-remove design exists but needs causal-stability-tracked tombstone garbage collection, real additional machinery this lab's scope doesn't call for.

## Verification performed

Same constraint as every milestone in this pass: no Docker here, so nothing below reaches a real Redis.

- **Full solution build**: 0 warnings, 0 errors.
- **`CartItemCrdtPropertyTests`** (8 facts, `Cart.IntegrationTests` - pure logic, needs no container): commutativity, associativity, idempotence, and merge-with-empty-is-a-no-op, each checked against 2,000 randomly generated states built by folding a random operation sequence (not arbitrary field values, which could describe a state `Merge` could never actually produce); `EffectiveQuantity` never negative under 2,000 random merges; the sequential add-then-remove-on-one-replica-never-resurrects case; and the two add-wins cases (a remove that observed nothing loses cleanly to a concurrent add; the Dynamo-paper scenario itself - an existing item, one replica removes what it saw, another concurrently increases it, the increase survives with the exact resulting quantity pinned).

  Record-synthesized equality could not be used for any of these: `HashSet`/`Dictionary` don't override `Equals` (reference equality), so two independently-built `CartItemCrdt` values with identical content compare unequal by C#'s own default. Every property test uses this file's own `StructurallyEqual`, comparing live/tombstone sets via `SetEquals` and counters key-by-key - a mistake here would have made the tests pass or fail for the wrong reason, so it's called out explicitly rather than left implicit in a helper nobody reads twice.
- **`CartCrdtStateTests`** (4 facts): the map-level composition specifically - merging disjoint SKUs keeps both, removing one SKU leaves siblings untouched, metadata snapshots once and stays put across a reinforcing increase, and the map-level version of add-wins.
- **`Services.ArchitectureTests`** (80/80) passes unchanged.
- **Not verified in this pass**: `CartStore.MergeAsync` against a real Redis (the Testcontainers-backed `CartStoreTests` suite this would belong in was not extended to cover it); the `/carts/me/merge` endpoint reached over real HTTP; two actual concurrent clients racing against a live cart.

## What was deliberately not done

- **Rewriting the existing PUT/DELETE cart routes to be CRDT-backed.** The larger, riskier rewrite; the ordinary single-session case those routes serve was already correct and already tested, and nothing about this milestone's problem required touching it.
- **Carrying dot identifiers to the client**, and the cross-session `Remove` gap that leaves open - see above.
- **Tombstone garbage collection.** `TombstoneDots` grows monotonically forever in this implementation; a long-lived cart with many add/remove cycles accumulates tombstones without bound. Real OR-Set implementations garbage-collect once every replica is causally known to have observed a tombstone (causal stability); this lab has no multi-node deployment for that to mean anything real, so it wasn't built.

## See also

- [Milestone 42: Cart Service with Redis as the System of Record](milestone-42-cart-redis-primary-store.md) — the hash-per-SKU storage model this milestone's merge path sits alongside without replacing.
- [Milestone 84: Every Service Gets a Door](../security/milestone-84-catalog-cart-inventory-authz.md) — the `/carts/me` routing and per-shopper identity this milestone's `CartStore.MergeAsync` reuses unchanged.
- [Milestone 85: The BFF Carries What the Domain Already Models](../domain/milestone-85-bff-checkout-completeness.md) — the cart version field this milestone's merge path also bumps, on the same `__version` hash entry.
