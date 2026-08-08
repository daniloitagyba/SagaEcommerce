# Milestone 85: The BFF Carries What the Domain Already Models

## Scope

`Storefront.Service` is the only checkout path a browser has, and it was the narrowest one in the system. `CheckoutOrderRequest` carried three fields - items, coupon code, and (before Milestone 83) a client-asserted customer id - while `Orders.Api` had accepted `shippingAddress` since Milestone 71 and `paymentMethod` since Milestone 68. Every storefront order fell back to `FlatShippingAmount` and the global tax rate, and was always Pix: the destination-based shipping/tax tables and the whole authorize/capture state machine were reachable only by a caller that bypassed the BFF entirely and posted straight to `/orders`.

Sharper still: `Orders.Api` has carried an `Idempotency-Key`-based dedup path since it was built, feature-flagged and tested - and the one caller that is a human with a mouse never sent the header. A double-clicked checkout button created two orders, charged twice, and reserved the stock twice.

And nothing anywhere compared what a cart's snapshotted prices said an order would cost against what the live catalog actually charged for it - `CartLineItem`'s own doc comment says checkout "is where prices get revalidated against the current catalog," which was true only in the sense that `OrderPricingService` silently repriced from Catalog; nothing compared the two numbers or told the shopper they differed.

## Design

**The BFF now forwards what it already fetches.** `CheckoutRequest` gains `paymentMethod` and `shippingAddress`, both passed straight through to `CheckoutOrderRequest` and Orders.Api's existing, already-tested handling of them - no new pricing or payment logic in Storefront, since none was missing on the Orders.Api side.

**A cart version, not a client-generated key, drives idempotency.** `CartStore` gains a monotonic counter, stored as an ordinary field in the cart's own Redis hash (`__version`, alongside the real line items) rather than a separate key - it lives and dies with the cart, no independent TTL to keep in sync, no orphaned counter once a cart expires. Every mutation (`UpsertItemAsync`, a successful `RemoveItemAsync`) increments it via `HashIncrementAsync`, atomic without a client-side read-modify-write. `GetCartAsync`'s response exposes it directly.

Storefront builds `Idempotency-Key: checkout:{subject}:{version}` from that number and the token's own subject claim - deterministic per *this exact cart state*, for *this shopper*. A double-submitted click carries the identical key and replays through Orders.Api's existing dedup path instead of creating a second order; adding or removing an item bumps the version, so a genuinely new checkout after editing the cart is never blocked by a stale key left over from an earlier attempt.

**The subject comes from reading the shopper's own token, unverified - deliberately.** `UnverifiedJwt.TryGetClaim` base64url-decodes the JWT payload segment and reads one claim, with no signature check at all. This is safe specifically *because* it is never used for authorization: Orders.Api and Cart.Service both verify the same forwarded token fully, against Keycloak's JWKS, the way Milestones 83-84 already require. If this layer misreads the subject, the worst case is a wrong idempotency key - a functional annoyance for one shopper, not a security boundary, since nothing downstream trusts this layer's opinion of who anybody is. Malformed input (no dots, invalid base64, a token that isn't even present) degrades to `null` rather than throwing - a BFF convenience function must not become a new way for checkout to fail.

**Price-change detection lives in the domain that already computes both numbers, not the BFF that can only compare after the fact.** The cart carries each item's snapshotted `UnitPrice`; Storefront sums it into an `expectedSubtotal` and sends it alongside the order. `CreateOrderHandler` compares it against `checkout.Breakdown.Subtotal.Amount` - the live-catalog-priced subtotal it already computed for this exact request - *before* the idempotency gate, for the same reason pricing itself runs before that gate: a replayed request must never re-litigate a price the first attempt already confirmed or rejected. A mismatch returns a new `PriceMismatch` result carrying both numbers; `Orders.Api` turns it into `409 Conflict` (not `400` - nothing about the request is malformed, the catalog moved under it, the same "well-formed but the world changed" reasoning Milestone 67 already established for a coupon losing its last redemption slot mid-request) with both figures in the response so a client can show "price changed: was 100.00, now 120.00" and let the shopper explicitly re-confirm rather than being silently charged a different amount.

**Deliberately the subtotal, not the grand total.** Shipping, tax, and discounts are expected to apply and to differ from whatever a cart last saw - that is what checkout is *for*, not a price change. Comparing the grand total would make every order with a coupon or a destination-based shipping charge look like a mismatch. The subtotal isolates exactly the one thing that can legitimately surprise a shopper: a product's own price moving between when they looked and when they bought.

## Verification performed

Same constraint as Milestones 81-84: no Docker, no live Keycloak or Redis reachable from this environment.

- **Full solution build**: 0 warnings, 0 errors.
- **`Orders.UnitTests`**: 199/199 passing, 3 new facts on `CreateOrderHandler` - a matching `ExpectedSubtotal` creates the order normally, a mismatched one is rejected with the order never persisted (`repository.AddCallCount` stays 0 - the important assertion, since a false negative here would mean the order was created *and* reported as failed), and a null `ExpectedSubtotal` skips the check entirely (existing callers that never send one are unaffected).
- **`Storefront.UnitTests`**: 13/13 passing (6 rewritten `CheckoutEndpointTests` plus 7 new `UnverifiedJwtTests` covering a real claim read, a missing claim, and every malformed-input shape degrading to null rather than throwing). The happy-path test now asserts the forwarded `Idempotency-Key` header's exact value, the echoed `paymentMethod`/`shippingAddress`, and the computed `expectedSubtotal` - not just that a 201 came back.
- **`Orders.ArchitectureTests`** (85/85) and **`Services.ArchitectureTests`** (80/80) pass unchanged.
- **Not verified in this pass**: a real double-click against a live Redis-backed cart and Postgres-backed idempotency store; the 409 response's actual JSON shape reaching a client; `HashIncrementAsync`'s atomicity under genuine concurrent cart mutations (correct per Redis's own guarantees for a single hash-field increment, but unexercised here beyond that).

## What was deliberately not done

- **A UI that actually shows the 409 and asks the shopper to re-confirm.** The response carries both numbers (`expectedSubtotal`, `actualSubtotal`) for a client to build that flow from; no client in this lab exists to build it into.
- **Retrying past a price-mismatch automatically at the new price.** Silently accepting whatever the server now charges defeats the entire point of asking - the response is a stop, not a suggestion.
- **A tolerance band on the subtotal comparison.** It is an exact match, not "within a centavo" - the arithmetic on both sides is already centavo-exact (`MoneyAllocation`'s whole-cent guarantee, Milestone 66/70), so a tolerance would only ever mask a real discrepancy, not accommodate a legitimate rounding difference that doesn't otherwise exist.

## See also

- [Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk](milestone-66-line-items-pricing-and-risk.md) — `OrderPricingService`'s server-side repricing, which this milestone finally lets the BFF's own checkout compare against.
- [Milestone 67: Coupons That Can Actually Run Out](milestone-67-coupon-lifecycle.md) — the "well-formed request, world changed underneath it" 409 pattern this milestone's price-mismatch response reuses.
- [Milestone 68: Authorize, Then Capture](milestone-68-authorize-capture.md) and [Milestone 71: The Customer Stops Being a String](milestone-71-customers-tiers-and-geography.md) — `paymentMethod` and `shippingAddress`, both already fully built on Orders.Api's side and now actually reachable through the storefront.
- [Milestone 83: The Shopper Stops Being Self-Asserted](../security/milestone-83-end-user-identity.md) — the forwarded bearer token this milestone reads a claim out of (without verifying it) to build an idempotency key.
