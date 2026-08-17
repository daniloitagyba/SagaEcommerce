# Milestone 26 AuthN/AuthZ and Zero-Trust

## Scope

`orders-api` had no authentication at all through Milestone 25 - anyone who could reach it could create or read any order. This milestone closes that at two independent layers: OIDC/JWT bearer authentication and role-based authorization inside Orders.Api itself (application identity - "which client is calling"), and a Linkerd `AuthorizationPolicy` restricting which mesh workload identities can reach it at all (workload identity - "which pod is calling"). Neither layer trusts the other; both were proven to actually enforce independently, not just installed.

## Design

- **Keycloak** (`compose/compose.yaml`), wired into K3s the same way Schema Registry was in Milestone 19: fixed IP on the `k3s-bridge` network, a selectorless K8s `Service` + `EndpointSlice` pair. Runs in `start-dev` mode with embedded storage rather than Postgres-backed - a deliberate scope boundary, same category as Milestone 22's in-memory saga state: this milestone is about proving OIDC/JWT validation and scope-based authorization actually work, not building Keycloak HA.
- **`scripts/keycloak-configure-realm.sh`**: idempotent realm/role/client provisioning against Keycloak's admin REST API - a `orders-lab` realm, `orders:read`/`orders:write` realm roles, an `orders-api-clients` confidential client with `client_credentials` enabled and a hardcoded-audience protocol mapper (`orders-api`, not the grant's default `account` audience). The client secret is fixed to `KEYCLOAK_CLIENT_SECRET` in `.env` (the same file `POSTGRES_PASSWORD` already lives in) rather than left for Keycloak to auto-generate, so every script that needs a token reads it from one known place.
- **Orders.Api**: `AddAuthentication().AddJwtBearer(...)` validates against Keycloak's own JWKS, fetched via OIDC discovery and refreshed automatically - no shared secret or key material in this service's own config. Two policies, `orders:read` (satisfied by either role) and `orders:write` (write role only), applied per-endpoint: `POST /orders` requires write; `GET /orders/{id}`, `/orders/summary`, and `/orders/{id}/history` require read.
- **Linkerd `AuthorizationPolicy`** (`kubernetes/cluster-policies/orders-api-authz.yaml`, applied imperatively like the Linkerd install and Kyverno policies from Milestone 25 - cluster infrastructure, not an Argo CD-managed application manifest): narrows `orders-api`'s mesh-visible inbound traffic to only requests authenticated as the `orders-worker` workload identity, tightening Milestone 24's cluster-wide `all-unauthenticated` default for the first time.
- **Every script that calls `/orders` directly now fetches a token first**, via the new `scripts/keycloak-get-token.sh`: `smoke-test.sh`, `saga-chaos-test.sh`, `resilience-chaos.sh`, and `k6-run.sh` (which passes it to `orders.js` as `ACCESS_TOKEN`, fetched once per run - the realm's 900s token lifespan comfortably covers every k6 profile, including the 5m20s `soak` run).

## What didn't work

**Every application pod in `orders-lab` shared the namespace's `default` ServiceAccount - making per-service Linkerd `AuthorizationPolicy` meaningless before it could even be written.** Linkerd derives mTLS identity from the pod's ServiceAccount; with every workload on `default`, `orders-api`, `orders-worker`, and any throwaway test pod would all present the *identical* identity, so no policy could distinguish a legitimate caller from an arbitrary one. Fixed by giving `orders-worker` its own dedicated ServiceAccount (`kubernetes/base/orders-worker-serviceaccount.yaml`) - the one genuinely plausible direct caller of `orders-api` in this architecture, even though nothing currently calls it that way (the real architecture is Kafka-mediated).

**A stale `images:` transformer in the deployed Kustomize overlay had pinned every application image to `:milestone-7` since Milestone 16 - silently discarding every rebuild this entire session (M17-25) that assumed `:latest`.** The base manifests default to `local-distributed-lab/orders-api:latest`, but `kubernetes/overlays/local/kustomization.yaml` - the overlay Argo CD actually reconciles - has its own `images:` block rewriting that to `newTag: milestone-7`, last touched in a Milestone 16 commit and never updated since. Building and importing a fresh `:latest` image (the obvious approach) had zero effect: the running pod's `imageID` never changed, because the deployed Rollout was never actually referencing `:latest` in the first place. Found by comparing `docker images`' short image ID against the running pod's full `imageID` field and noticing they matched a build from hours earlier. Fixed by rebuilding through the established path instead - `docker compose build migrations` (the actual buildable target sharing `Orders.Api`'s Dockerfile, per `k3s-build-images.sh`'s own convention) - which correctly produces and tags `:milestone-7`, matching what the overlay has expected all along.

**Keycloak realm roles live in a nested `realm_access.roles` claim, which nothing in the default ASP.NET Core JWT pipeline understands - so `RequireRole()` failed even for a token that genuinely had the role.** First live test: no token correctly returned `401`, but a *valid* token with the correct `orders:write` role returned `403`, not `201`. `RequireRole()` checks for `ClaimTypes.Role` claims specifically; Keycloak's realm roles arrive as a single JSON-object claim (`realm_access: { "roles": [...] }`), which the JWT bearer handler doesn't unpack into individual role claims by default - a well-known Keycloak/ASP.NET Core integration gap, not a bug in either side. Fixed with a `JwtBearerEvents.OnTokenValidated` handler that parses `realm_access` and adds each entry as a proper `ClaimTypes.Role` claim before authorization runs.

**The first negative test of the Linkerd `AuthorizationPolicy` was a false pass, for a second reason after Milestone 25's glob mistake - Linkerd automatically exempts a pod's own liveness/readiness probe paths from any `AuthorizationPolicy`.** A throwaway pod using the untrusted `default` identity curled `orders-api`'s `/health/live` expecting a rejection - it got `200`. `linkerd diagnostics policy` revealed why: alongside the `orders-worker`-only rule, Linkerd auto-generates an *implicit*, unauthenticated-allowed rule scoped exactly to whatever HTTP paths the pod's own `livenessProbe`/`readinessProbe` config points at - a deliberate, sensible feature (so adding a policy can never accidentally lock out kubelet, which has no way to present an mTLS certificate), but it meant this specific path was never a real test of the policy at all. Retested against `/orders/summary` (a real, non-exempted app route) instead: the untrusted identity got a clean `403 Forbidden` directly from Linkerd's proxy - the request never reached the application.

## Results

Layered, live-tested, in this order:

```
$ curl -X POST http://orders-api/orders            # no token
401

$ curl -X POST http://orders-api/orders -H "Authorization: Bearer $WRITE_TOKEN"
201

$ curl http://orders-api/orders/$ORDER_ID -H "Authorization: Bearer $WRITE_TOKEN"
200

$ curl -X POST http://orders-api/orders -H "Authorization: Bearer $READONLY_TOKEN"   # orders:read only
403

$ curl http://orders-api/orders/summary -H "Authorization: Bearer $READONLY_TOKEN"
200
```

Linkerd's independent layer, tested against a real (non-probe-exempt) route:

```
# pod using ServiceAccount "orders-worker" (trusted identity)
$ curl http://orders-api/orders/summary
401   # reached the app - rejected by Orders.Api's own JWT check, not Linkerd

# pod using ServiceAccount "default" (untrusted identity)
$ curl http://orders-api/orders/summary
403   # rejected by Linkerd itself - never reached the app
```

The `401` in the first case is the actual proof the mesh layer worked: Linkerd let the trusted identity's request through to the application, which then applied its own, completely independent authorization check and correctly rejected the tokenless request. Two layers, two different rejection reasons, neither one covering for the other.

### Regression check

`dotnet test`: 24 unit + 7 integration, all passing. `k3s-smoke-test.sh` and `k6-run.sh smoke`: passing, now authenticating via `scripts/keycloak-get-token.sh` on every run. A first `k6 smoke` run immediately after the auth rollout crossed its `get-order` p95 latency threshold (571ms vs. 500ms) - a cold-start artifact (JIT warmup, fresh connection pools, first JWKS fetch) from the string of rebuilds/restarts immediately preceding it, not a persistent cost: a second run measured `get_p95_ms=4.6`, `create_p95_ms=7.0` - JWT validation's steady-state overhead is noise-level once the JWKS response is cached.

## Running the experiment

```bash
# Get a token and exercise the API directly
TOKEN=$(scripts/keycloak-get-token.sh)
curl -X POST http://<orders-api>/orders -H "Authorization: Bearer $TOKEN" \
  -d '{"customerId":"demo","items":[{"sku":"SKU-BOOK-002","quantity":1}]}'

# Prove the Linkerd layer independently (needs a throwaway pod using
# ServiceAccount "orders-worker" vs one using "default" - see
# kubernetes/cluster-policies/orders-api-authz.yaml for the policy itself)
linkerd diagnostics policy -n orders-lab po/<an-orders-api-pod> 8080
```
