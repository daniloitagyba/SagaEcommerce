# Milestone 84: Every Service Gets a Door

## Scope

Milestone 83 closed the ownership gap in Orders.Api. Three services never had *any* authentication to begin with:

- **Catalog.Service**: `POST /products` and `POST /categories` were open to anyone who could reach the pod. A product's price is exactly what `OrderPricingService` prices a real order from - an unauthenticated write here is not a content-management gap, it is a way to inject a price into a real checkout.
- **Cart.Service**: `cartId` was a client-supplied opaque string with no owner. Anyone who could guess or enumerate one could read or empty another shopper's cart.
- **Inventory.Service**: `GET /inventory` returned exact `AvailableQuantity`/`ReservedQuantity` for the entire catalog to anyone, unauthenticated - a competitor's scraper reads your sell-through rate for free.

This milestone closes all three, each with the fix that fits its actual threat model rather than a uniform "add a role check" pass.

## Design

**Catalog: writes are gated, reads stay open.** `catalog:admin` behind `POST /products` and `POST /categories`; every `GET` is untouched. Browsing a catalog is not a privileged action - only asserting what's *in* it is.

**Cart: the fix is removing the input, not validating it.** A `cartId` guard (`RequireAuthorization()` plus a comparison against the caller's identity) would have been the smaller diff, but it leaves the enumeration surface intact for anyone who *is* authenticated as someone else. Instead, the storage key is derived from the authenticated caller's own identity (the same `preferred_username`-first read Milestone 83's `CallerIdentityExtensions` uses, duplicated locally since Cart.Service has no reference to Orders.Api's assembly - the same tradeoff this codebase already made for the JWT-bearer wiring itself, which is now four services' worth of near-identical `Program.cs` boilerplate rather than a shared abstraction). The routes themselves changed shape to match: `/carts/{cartId}/items/{sku}` becomes `/carts/me/items/{sku}` - there is no cartId left to be wrong about, so the URL stops pretending one is a meaningful input. One cart per authenticated shopper, matching how carts actually work everywhere real e-commerce ships one; anonymous cart-building is the explicit trade this makes (see "What was deliberately not done").

**Inventory: the list and the lookup have different threats, so they get different answers.** `GET /inventory` (every SKU's exact counts in one response - the actual scraping target) requires `inventory:read`. `GET /inventory/{sku}` (one shopper checking one product) stays open to anyone, but an unauthenticated caller now receives a coarse `AvailabilityBand` (`InStock` / `Low` / `OutOfStock`, cut at a fixed low-stock threshold of 5 units) instead of the two exact integers; a caller holding `inventory:read` still gets the exact numbers the way every internal consumer of this endpoint already expects. This is also what a real storefront shows an anonymous shopper - a stock badge, not a number - so `Storefront.Service`'s own hedge call to Inventory (which is not authenticated, and deliberately stays that way; see below) now surfaces exactly that badge to the browser as a side effect of the fix, not a separate change.

**Every service now validates its own audience.** Rather than one shared `"orders-api"` audience letting a token minted for any purpose work everywhere, `scripts/keycloak-configure-realm.sh` now mints a distinct `oidc-audience-mapper` per target service (`orders-api`, `catalog-service`, `inventory-service`, `cart-service`) on each client, via a `create_audience_mapper` function that replaces what was, after Milestone 83, already two near-identical inline blocks. A token scoped to Cart.Service cannot be replayed against Inventory.Service's write-adjacent routes.

**Storefront's proxy layer forwards whatever `Authorization` header it received, uniformly, rather than deciding per-route whether to.** `ProxyEndpoints.ForwardAsync` (the generic catalog/cart passthrough) now relays an inbound `Authorization` header when present - a no-op for Catalog's still-anonymous `GET`s, load-bearing for Cart now that it requires one. `StorefrontEndpoints.CheckoutAsync` calls `/carts/me` instead of `/carts/{cartId}`, using the same forwarded shopper token Milestone 83 already required for the order-creation call - one token, two services, both validating it themselves.

**`KeycloakTokenProvider`'s deletion in Milestone 83 turned out to be exactly right for this milestone too**: nothing in Storefront ever needed to authenticate *as itself* again, for either service it fronts.

## Verification performed

Same constraint as Milestones 81-83: no Docker here, so nothing below reaches a real Keycloak, a real Redis-backed cart, or a real Mongo-backed catalog.

- **Full solution build**: 0 warnings, 0 errors, across all seven services.
- **`Orders.UnitTests`** (196/196) and **`Storefront.UnitTests`** (6/6, one test removed - `MissingCartIdFailsValidation`, since there is no longer a cartId to be missing) both pass. `CheckoutEndpointTests`' happy-path case now also asserts both cart calls (`GET` and `DELETE /carts/me`) carry the forwarded shopper token, not just the order call.
- **`Orders.ArchitectureTests`** (85/85) and **`Services.ArchitectureTests`** (80/80) pass unchanged.
- **`scripts/keycloak-configure-realm.sh`**: passes `bash -n`; not run against a live Keycloak. The script's own idempotency (every block checks for an existing role/client/mapper/user before creating one) was preserved by construction - the new `create_audience_mapper` and `assign_missing_roles` functions are the same check-then-create shape the file already used twice, refactored into one implementation rather than four.
- **Not verified in this pass**: any of the three services' JWT validation against a real token; `Cart.Service`'s new routes reachable through a live Storefront proxy; the availability-band cutover actually changing what an unauthenticated `curl` sees; `Catalog.IntegrationTests`/`Inventory.IntegrationTests` re-run (neither exercises the HTTP endpoint layer directly - both test Kafka message processors and MongoDB/EF repositories - so they were unaffected by this milestone's changes by construction, not because they were run and passed).

## What was deliberately not done

- **Anonymous cart-building.** A real storefront lets a shopper add items before signing in; this milestone's fix requires authentication for every cart operation, which is a real UX regression this lab accepts rather than papers over. Supporting both would mean a genuine guest-cart-to-authenticated-cart merge (the same class of problem Milestone 86's CRDT cart work is adjacent to, not identical to it) - out of scope here.
- **Rate limiting or abuse detection on the now-anonymous per-SKU inventory lookup.** Coarsening the response removes the *value* of scraping it; it does not stop the scraping itself. Milestone 11's load shedding is the closest existing mechanism and was not extended to target this route specifically.
- **A shared `BuildingBlocks` project for the JWT-bearer wiring**, now duplicated near-verbatim across four `Program.cs` files. Named explicitly as a cost of this milestone's approach, not missed - see Design.
- **`scripts/cart-redis-durability-test.sh`.** Its whole method is creating hundreds of distinct carts under distinct client-chosen `cartId`s to measure how many distinct Redis keys survive a kill - a shape that no longer exists once a cart's key is the caller's own identity rather than a value the caller supplies. Left unfixed rather than hastily reworked into something that measures a different property than it was built to; still calls the pre-Milestone-84 route shape and will fail outright until it's redesigned (most plausibly to write synthetic keys directly against Redis, bypassing Cart.Service's API, since that's what it actually needs to exercise).

## See also

- [Milestone 26: AuthN/AuthZ and Zero-Trust](milestone-26-authn-authz.md) — the JWKS-backed `AddJwtBearer` pattern and `realm_access.roles` unpacking this milestone copies into three more services.
- [Milestone 83: The Shopper Stops Being Self-Asserted](milestone-83-end-user-identity.md) — `CallerIdentityExtensions`' `preferred_username`-first identity read, duplicated here for Cart.Service, and the Storefront token-forwarding this milestone extends to a second downstream service.
- [Milestone 44: Bestsellers Projection via Redis Sorted Sets](../architecture/milestone-44-bestsellers-redis-sorted-sets.md) — Catalog's existing Redis-backed read path, unaffected by this milestone since only writes moved behind auth.
