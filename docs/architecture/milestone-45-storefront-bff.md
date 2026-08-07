# Milestone 45: Storefront UI as a Backend-for-Frontend

## Scope

The final milestone of the e-commerce expansion, and the one that closes the loop on the very first question this expansion started with: a home page listing products by category and by bestseller. Everything built across M40-M44 (Catalog on MongoDB, Inventory reservations, Cart on Redis, a 4-step saga with compensation, bestsellers on Redis sorted sets) was validated via `curl` and Kafka topic inspection - real, but not from an actual user-facing surface. This milestone puts a real browser UI on top and, in the process, exercises the whole stack end to end through it.

## Why this is a backend-for-frontend, not a static site behind nginx

`orders-api-clients` is a **confidential** Keycloak client - `client_credentials` grant, secret required (`scripts/keycloak-configure-realm.sh`). That secret can never reach the browser. A plain static site calling `POST /orders` directly from client-side JavaScript has no way to authenticate that request without embedding the secret in code the browser can read - a real security defect, not a style preference.

`Storefront.Service` solves this the standard way: it's a real ASP.NET Core service that serves the static `wwwroot` files *and* sits between the browser and every backend it talks to.

- `GET /api/catalog/*` and `GET|PUT|DELETE /api/cart/*` are thin, generic forwards to Catalog.Service and Cart.Service - no auth needed on either, so no special handling.
- `POST /api/orders` is different: `KeycloakTokenProvider` fetches and caches a `client_credentials` token server-side (a `SemaphoreSlim`-guarded cache, refreshed ~30 seconds before expiry, with a 5-second floor so a pathological `expires_in` can't cause a fetch storm), and the proxy attaches it as a real `Authorization: Bearer` header before forwarding to Orders.Api. The browser never sees Keycloak exists.
- Same origin throughout - the browser only ever talks to Storefront.Service, so there's no CORS configuration to get right or wrong across three different backend origins.

## A real bug a test caught before the cluster did

`KeycloakTokenProviderTests` (a fake `HttpMessageHandler`, no real Keycloak) exists specifically to test the caching behavior - and immediately failed on the very first run with the access token deserializing to `null`. The cause: Keycloak's token response is snake_case (`access_token`, `expires_in`), but the code used `JsonSerializerDefaults.Web`, which only maps camelCase. `AccessToken`/`ExpiresIn` silently matched nothing and deserialized to their default values. Fixed with explicit `[JsonPropertyName]` attributes rather than a serializer-wide option, since this is the only place in the service that talks to a non-camelCase API.

Writing the second test (refetch-after-expiry) surfaced a second, more interesting fact about the code under test: the cache window is floored at 5 seconds (`Math.Max(expiresIn - 30, 5)`) specifically so a misbehaving or misconfigured token endpoint can't cause a refetch on every single request. That floor meant no `expires_in` value could make a cached token go stale on the *next* call in real time - the test needed an injectable `TimeProvider`, advanced past the floor programmatically, rather than a real `Task.Delay`. The fix made the test both correct and fast (no sleep), and made the provider itself testable in a way it wasn't before.

## A second real bug, found live: Linkerd rejected the checkout call in ~6ms

Everything else worked on the first deploy - home page, bestsellers, add-to-cart, all proxied correctly. Checkout returned a real `403` from `orders-api`, in about 6 milliseconds - too fast to be application logic. `orders-api-http`'s Linkerd `AuthorizationPolicy` (Milestone 26, with a first real regression already documented against it in Milestone 31) only allows two callers: `orders-worker`'s mesh identity, and host-originated traffic from the k3s node bridge gateway. `Storefront.Service` was a third, brand-new in-mesh caller with neither - and was additionally running under the `default` ServiceAccount (no `serviceAccountName` set), which a Linkerd policy can't scope to without allowing every other pod that also happens to use `default`.

This is the exact same class of finding Milestone 31 already left a comment about in this same file: a new legitimate caller of a mesh-authorized `Server` needs an explicit allow entry, or it's a 403 at the mesh layer before the application ever sees the request. Fixed with a dedicated `storefront-service` `ServiceAccount` (matching `orders-worker`'s existing pattern) and a third `MeshTLSAuthentication`/`AuthorizationPolicy` pair, OR'd with the two already there - multiple `AuthorizationPolicy` resources targeting the same `Server` combine as OR, so this adds a path without loosening the existing one.

## Design notes and honest boundaries

- **Cart contents don't (yet) drive which SKU gets reserved.** *(Superseded by Milestone 66: `POST /orders` now accepts line items, prices them server-side against the catalog, and the saga reserves a real SKU; Storefront.Service's `POST /api/storefront/checkout` reads the shopper's actual cart and submits it that way, replacing the `{customerId, amount: cart.total, currency}` call this bullet originally described.)* `Order` (Milestone 7) is still amount-only - checkout sends the cart's `total` as `amount`, and Milestone 43's `SagaSkuMapper` still deterministically hashes the `OrderId` to pick a demo SKU, same as before this milestone. This is the same boundary M43 already documented as deliberately out of scope (a real line-item rewrite is bigger and riskier than what any of these milestones individually set out to prove); the storefront makes that boundary more visible, not different.
- **No profiler baked into this Dockerfile**, unlike every other service. Storefront.Service has no CPU-intensive business logic - it's a thin proxy and a static file host - so continuous profiling has little to show here, and skipping it keeps the image build simpler. A deliberate omission, not an oversight.
- **The home page falls back gracefully.** Before any sale has happened in a category, `GET /products/bestsellers?category=X` returns an empty list; the frontend detects that and falls back to a plain `GET /products?category=X` listing rather than showing an empty section - a demo of the system should never look broken just because nobody has bought anything yet.

## Live results

Full flow exercised against the live ClusterIP:

- `GET /` serves the real `index.html`.
- `GET /api/catalog/products/bestsellers?limit=3` correctly proxies to Catalog.Service and returns live-ranked products (real purchase history from M44's testing, e.g. `SKU-BOOK-002` at 4 units sold).
- `PUT /api/cart/carts/{id}/items/{sku}` correctly proxies to Cart.Service, price-snapshotting from the real Catalog data.
- `POST /api/orders` - after the Linkerd fix - returns a real `201 Created` with a genuine order ID, having attached a server-fetched Bearer token the client never saw. `storefront-service` pod logs confirm the full chain: token fetch (`200` in ~9ms) → forward to `orders-api` (`201`), no manual intervention.
- **Unit tests**: `KeycloakTokenProviderTests` (2/2 passing) - single-fetch caching and TimeProvider-driven refetch-after-expiry, both real bugs caught during development, not retrofitted after the fact.
- **Regression check**: `scripts/k6-run.sh smoke` post-deploy - `failed_rate=0`, `checks_rate=1`, `flow_rate=1`.

## Running it

```bash
kubectl port-forward -n orders-lab svc/storefront-service 8090:80
```

Then open `http://127.0.0.1:8090` in a browser.
