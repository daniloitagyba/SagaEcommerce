# Frontend, Catalog.Service and Infra Audit — 2026-08-15

A follow-up to [`audit-2026-08-14-service-and-business-rule-review.md`](audit-2026-08-14-service-and-business-rule-review.md),
which covered Orders/Payments/Inventory/Cart/Storefront *business rules* in
depth but explicitly left three areas unexamined: the React frontend
(`apps/storefront-web`), `Catalog.Service` itself (only touched tangentially,
as the thing `OrderPricingService` calls), and the deployment/infra layer
(`kubernetes/`, `compose/`, `scripts/`).

This pass covers those three, plus a mechanical sweep of the whole C# backend
for dead code, debug leftovers, and common footguns (empty catches,
`async void`, sync-over-async, commented-out code, `[Obsolete]`). The backend
sweep came back clean — no findings worth listing.

Scope note: this is an **assessment**, not an implementation pass. Findings
below are ranked by severity within each area; two are P0 and worth acting on
before anything else here.

---

## The two P0s

### 1. `Authentication__Authority` missing from 4 of 8 authenticated K8s workloads — crash-loops on deploy

`cart-service.yaml`, `catalog-service.yaml`, `inventory-service.yaml`,
`payments-service.yaml`, and their migration/seed Jobs
(`payments-migration-job.yaml`, `inventory-migration-job.yaml`,
`inventory-seed-job.yaml`, `catalog-seed-job.yaml`) never set
`Authentication__Authority` in `kubernetes/base/`. Only `orders-api.yaml`
sets it. `compose/compose.yaml` sets it explicitly for every one of these
same eight workloads (lines 652–966) — this is Compose/K8s config drift, the
exact bug class already documented four times in this repo's own history
(`Redis__ConnectionString`, four separate Kafka `BootstrapServers` sections,
and `Authentication__Authority` itself on `orders-api` per
`docs/saga/milestone-75-*`).

Confirmed by reading the code, not just grep: all four services call
`AddKeycloakJwtBearer(...)` in `Program.cs` (Cart:58, Catalog:63,
Inventory:213, Payments:207) **before** their `--migrate`/`--seed` argument
check (Catalog:72, Inventory:223/231, Payments:217). `KeycloakJwtBearerExtensions.AddKeycloakJwtBearer`
(`apps/src/BuildingBlocks.WebAuthentication/KeycloakJwtBearerExtensions.cs:47-48`)
does:

```csharp
var authority = configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
```

unconditionally, during service registration. So this doesn't just break the
running service — it crashes the migration and seed Jobs too, before they
ever reach their `--migrate`/`--seed` branch.

**Impact if `kubernetes/base` (or the `local` overlay Argo CD points at) is
applied today**: `cart-service`, `catalog-service`, `inventory-service`,
`payments-service` Deployments enter CrashLoopBackOff immediately.
`catalog-seed-job`, `inventory-migration-job`, `inventory-seed-job`,
`payments-migration-job` all fail immediately, so Inventory and Payments
never even get their schema created. Only `orders-api` comes up cleanly;
`orders-worker`/`storefront-service` come up too, but only because neither
calls `AddKeycloakJwtBearer` at all.

**Fix**: add `Authentication__Authority: http://keycloak:8080/realms/orders-lab`
(matching Compose) to all eight manifests. Given this exact bug has recurred
four times, worth a one-time CI check (a script asserting every K8s manifest
whose paired Compose service sets a given env var also sets it) rather than
trusting the next add-a-service-manually pass not to repeat it a fifth time.

### 2. Catalog.Service's unique indexes are only created via `--seed`, never on a normal boot

`Program.cs:72-81`:

```csharp
if (args.Contains("--seed", StringComparer.Ordinal))
{
    ...
    await productRepository.EnsureIndexesAsync(CancellationToken.None);
    await categoryRepository.EnsureIndexesAsync(CancellationToken.None);
    ...
}
```

A normal `dotnet Catalog.Service.dll` startup (no args — every Deployment/
container in both Compose and K8s) never calls `EnsureIndexesAsync`. On a
freshly provisioned MongoDB where `--seed` is skipped, run against a
different database, or simply hasn't happened yet, `sku` has **no unique
constraint at all**.

Consequences, both real and immediate:
- Two products can be created with the same SKU. `ProductRepository.FindBySkuAsync`
  — the exact method `OrderPricingService` calls to price every checkout
  line — returns whichever duplicate Mongo happens to return first,
  **silently mispricing orders**.
- `ProductEndpoints.CreateAsync`'s `DuplicateKey` → 409 handling
  (`ProductEndpoints.cs:162-169`) is dead code without the index; nothing
  ever throws `MongoWriteException` with that error code.
- Every SKU/category-slug lookup is an unindexed collection scan.

**Fix**: call `EnsureIndexesAsync` unconditionally at startup (idempotent —
`CreateIndexAsync` no-ops if the index already exists), not gated on
`--seed`. Keep the `--seed` branch for data, drop it for indexes.

---

## Catalog.Service — the rest

Beyond the P0, `Product`'s invariants aren't actually enforced the way they
look:

- **`Product.Create` validates `price > 0`, but the seeder bypasses it
  entirely** — `CatalogSeeder.cs` builds every seed product via
  `new Product { ... }` object initializers (public setters,
  `Product.cs:54-70`), never through `Product.Create`. `ProductRepository.InsertAsync`
  doesn't revalidate either. Today's seed values are fine; the type doesn't
  actually stop a future script or admin tool from persisting a zero-price
  or blank-SKU product the same way.
- **No update endpoint exists for `Product` or `Category` at all** — only
  `GET`/`POST`. There's no supported way to correct a catalog price short
  of a full reseed or a raw Mongo write that bypasses every validation path
  above.
- **The seeder's idempotency check is keyed to the wrong collection** —
  `CatalogSeeder.SeedAsync` only checks whether *any category* exists and
  no-ops if so; it never checks `products`. A partial cleanup (categories
  survive, products don't) makes `--seed` silently skip repopulating
  products forever.
- **`CategorySlug` on a product is never validated against real
  categories** — `CategoryRepository.FindBySlugAsync` exists but is called
  from nowhere in the repo. A product can reference a nonexistent category,
  becoming invisible to category browsing while still fully priceable by
  SKU.
- **No currency whitelist** — `Product.Create` upper-cases and stores any
  non-blank string. A typo like `"USDD"` persists successfully and only
  surfaces later as a customer-facing checkout failure in
  `OrderPricingService.PriceAsync` ("catalog returned an unknown currency").
- **Inconsistent duplicate-key handling** — `ProductEndpoints` catches
  `DuplicateKey` → 409; `CategoryEndpoints.CreateAsync` has no equivalent
  catch, so a duplicate slug falls through to a generic 500 once the index
  from the P0 fix actually exists.
- **No input-size bounds** — `Name`/`Description`/`Attributes`/`Images` have
  no length or count limits; the seeder itself embeds multi-KB base64 SVGs
  per product, demonstrating the pattern with nothing stopping a much larger
  payload from approaching Mongo's 16 MB document cap.
- **No outbox/event publication at all** — Inventory, Payments and the whole
  Orders stack all have inbox/outbox infrastructure; Catalog has none. The
  SKU linkage `Inventory.Service`'s own seeder comment describes ("stock
  quantities mirror Catalog.Service's seeded SKUs") is a manual convention
  with no runtime reconciliation. Only masked today because there's no
  product-update path yet (see above) — the moment catalog data becomes
  mutable, nothing downstream learns about it.
- **`GetBestsellersAsync` makes up to 50 sequential Mongo round-trips**
  (`ProductEndpoints.cs:90-115`) — one `FindBySkuAsync` per ranked SKU
  instead of a single `$in` batch query, a pattern `ProductRepository.FindByIdsAsync`
  already demonstrates in the same file.
- **SKU matching is case-sensitive with no normalization** — `Sku` is
  `Trim()`med but not case-folded, so `"SKU-001"` and `"sku-001"` are
  distinct documents once the unique index exists.
- `MongoHealthCheck` does run a real `ping` (correctly detects a genuinely
  down Mongo), but never checks that the collections/indexes it depends on
  are actually usable — a deployment missing the indexes above would still
  report healthy.

---

## `apps/storefront-web` (frontend)

**1. [P1, bug] "Add to cart" silently overwrites quantity instead of adding
to it.** `ProductDetailPage.tsx:52-55` always sends the local, page-scoped
quantity (default `1`) as an absolute value via `PUT /cart/carts/me/items/{sku}`.
`CartEndpoints.cs:90-95` confirms this is a *set*: `item = existing.WithQuantity(request.Quantity)`.
A shopper with 3 of a SKU already in their cart who revisits the product page
and clicks "Add to cart" ends up with **1**, not 4 — silent data loss on the
core purchase flow. `CartPage.tsx`'s own quantity field uses the same
mutation correctly, because there the semantics genuinely are "set the exact
quantity"; it's specifically the *product page's* "Add to cart" button whose
label doesn't match what it does.

**2. [P1/P2, security] The OIDC token still lands in `sessionStorage`,
despite the code's own stated intent.** `tokenStore.ts` keeps the access
token in a module-level variable with a comment explaining this is
deliberately "outside React state" — implying an XSS-hardening goal. But
`oidcConfig.ts` never sets a `userStore`, so `oidc-client-ts` falls back to
its documented default of `window.sessionStorage`, writing the full OIDC
user object (access token, ID token, profile) there anyway under an
`oidc.user:...` key. The in-memory design doesn't achieve its apparent goal
because the underlying library persists the same token through a different
path.

**3. [P2, race] Checkout's "Accept & retry" button has no loading/disabled
guard**, unlike every other submit button in the app (`CheckoutPage.tsx:102-104`
vs. the main "Place order" button, the cancel button, and the return button,
which all correctly pass `loading={...}`). A double-click fires two
concurrent checkout mutations; the backend's cart-version-derived idempotency
key likely prevents a double charge, but the two `onError`/`onSuccess`
callbacks can still race each other in the UI.

**4. [P2, race] The cart quantity field fires one mutation per keystroke**,
with no debounce and no in-flight guard (`CartPage.tsx:66-73`). Typing "10"
sends `PUT` with qty=1 then qty=10 in quick succession; whichever response
lands *last* wins the query-cache write, not whichever request was sent
last.

**5. [P2, UX] The checkout summary doesn't refetch the cart after a
Price-Changed 409**, even though the backend's reprice-and-retry
(`StorefrontEndpoints.cs`) has typically already mutated the cart's stored
prices by the time this 409 reaches the frontend at all. `CheckoutPage.tsx:46-53`
only sets local `priceMismatch` state; the "Order summary" panel keeps
rendering the pre-mismatch cart from the stale query cache, so the banner and
the summary panel can show different numbers, and "Accept & retry" resubmits
without ever showing the shopper the price they're actually agreeing to.

**6. [P3, dead code] `useClearCart` is exported but never called from any
component** (`cart.ts:54-60`) — only referenced from its own test. The
backend already clears the cart post-checkout server-side, so this is either
removable or a missing "clear cart" affordance on `CartPage.tsx`.

**7. [P3, drift] `PriceMismatchProblem` is a named type that's never used** —
`isPriceMismatch` (`client.ts:38-46`) re-declares the same shape inline
instead of using the type from `types.ts:128-134`. Two sources of truth for
one wire contract.

**8. [P3, type safety] `error.response!.data` isn't actually backed by the
type predicate** that produced `error` (`CheckoutPage.tsx:48`) — safe today
because of a separate runtime check a few lines up, fragile to a future edit
of `isPriceMismatch`.

**9. [P3, config] No validation of required `VITE_KEYCLOAK_URL`/`VITE_KEYCLOAK_REALM`**
at build or startup (`oidcConfig.ts:9-10`) — a missing env var silently
produces an authority like `undefined/realms/undefined` instead of failing
fast.

**10. [P3, test gap] Zero test coverage for `ProductDetailPage.tsx`,
`ReturnDialog.tsx`, `OrderDetailPage.tsx`, `OrdersPage.tsx`, `Layout.tsx`** —
notably, finding #1 lives exactly in the one of these with no test.

Confirmed clean: no `any`, no hardcoded secrets, no committed `.env` with
real values, error-surfacing is otherwise thorough, and the backend's
reprice-and-retry is correctly *not* redundant with the frontend's own 409
handling — the frontend only ever sees this 409 after the server has already
exhausted its own retry.

---

## Infra / deployment

Finding 1 above (`Authentication__Authority`) is the headline item here. The
rest:

**2. [P1] `scripts/infra/k3s-deploy.sh` only force-redeploys 3 of 7 app
services.** It deletes the two migration Jobs it knows about
(`orders-migrations-m7`, `payments-migrations-m12`) and `kubectl rollout
restart`s only `orders-worker`/`payments-service`, patching `orders-api`'s
Rollout. `catalog-seed-m40`, `inventory-migrations-m41`, `inventory-seed-m41`
are never deleted, and `catalog-service`, `inventory-service`,
`cart-service`, `storefront-service` are never restarted — even though
`k3s-build-images.sh` builds fresh `:local` images for all seven and the
`local` overlay pins `imagePullPolicy: Never` for all seven. An operator who
edits Catalog/Inventory/Cart/Storefront code, rebuilds, and redeploys sees
"success" while those four Deployments keep serving the old image
indefinitely — `kubectl apply` is a silent no-op on an unchanged Deployment
spec under a static tag.

**3. [P2] `scripts/ops/expand-contract-backfill.sh` has no confirmation,
dry-run, or environment check.** It runs an unbounded backfill `UPDATE`
loop directly against Postgres via `docker compose exec`, with no preview
or abort path — contrast with `scripts/ops/dlq-redrive.sh`, which documents
a `--dry-run` flag for exactly this reason. Run against the wrong checkout
(e.g. pointed at the shared lab server's live volume) with no safety net.

**4. [P2] The tunables this session's audit fixes added
(`Outbox:ClaimWindowSeconds`, `Outbox:PendingSampleIntervalSeconds`,
`AntiEntropy:SweepIntervalSeconds`/`BatchSize`/`ProjectionLagThresholdSeconds`)
have no operator-facing env var anywhere** in Compose or K8s — sane
defaults, so not a correctness bug, but there's no way to widen
`ClaimWindowSeconds` under a slow broker, or shorten
`ProjectionLagThresholdSeconds` for a demo, without a code change and
rebuild.

**5. [P3]** `kubernetes/overlays/local/infrastructure-endpoints.yaml`'s
EndpointSlices are excluded from Argo CD sync by `argocd-cm`'s
`resource.exclusions` — Argo reports Synced while nothing is actually
applied. Already documented at the top of the file (a Milestone 40
incident), so a known, not new, sharp edge — flagged only because nothing
enforces that the next person adding an EndpointSlice there reads that
comment first.

**6. [P3]** `postgres-ha` backup/restore templates have no equivalent
restart-forcing step in `k3s-deploy.sh` — applied manually per `docs/`, so
this is a scope note, not a bug.

Confirmed clean: no raw `Secret` manifests or plaintext credentials anywhere
in `kubernetes/base` (the one Secret is a proper Bitnami `SealedSecret`);
every Compose password is `${VAR:?required}`-gated. Resource
requests/limits and all three probe types (liveness/readiness/startup) are
present and consistent across every app Deployment. `Kafka__BootstrapServers`
is consistent between Compose and K8s for every service — the
previously-documented bug class appears genuinely fixed for that one option,
just not generalized into a check that would catch its recurrence elsewhere
(see Finding 1). Every Compose profile is documented in the README; no
orphaned services. `network-policies.yaml`'s default-deny + `part-of` label
allow-list covers every Deployment.

---

## Suggested priority

1. **`Authentication__Authority`** in the 8 missing K8s manifests — cheap,
   mechanical, and currently means half the real cluster can't come up at
   all.
2. **Catalog.Service index creation** moved out of the `--seed` gate — cheap,
   and the silent-duplicate-SKU / mispriced-checkout scenario it currently
   allows is a real, not hypothetical, correctness risk.
3. **Frontend finding #1** (add-to-cart overwrite) — a live, easily
   reproduced bug on the core purchase flow.
4. Everything else here is real but lower urgency — worth a deliberate pass,
   not an emergency one.
