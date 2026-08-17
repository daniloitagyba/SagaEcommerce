# Idempotency-Key for POST /orders

## Scope

Tier 3 hardening item: `POST /orders` had zero protection against a client's own retry creating a duplicate order (a dropped response after a successful create is the classic case - the client can't tell whether the create landed, so a naive retry duplicates it). This adds an optional `Idempotency-Key` request header: a retried request with the same key returns the original order instead of creating a new one.

## Design

- Mirrors `RedisOrderCache`'s existing lock-with-retry-then-degrade pattern (`apps/src/Orders.Infrastructure/Caching/RedisOrderCache.cs`) rather than inventing a new one: same distributed-lock shape (`IDatabase.LockTakeAsync`/`LockReleaseAsync`), same resilience-pipeline-wrapped initial read, same philosophy that Redis unavailability degrades to "proceed without the guarantee" rather than failing the request - idempotency here is defense-in-depth, not the sole consistency mechanism.
- Header is optional. Omitting it preserves the exact prior behavior (always creates). `CreateOrderCommand.IdempotencyKey` defaults to `null`.
- On a genuine first request: `201 Created`, same body as before.
- On a replay (same key seen before, TTL 24h): `200 OK`, `Idempotency-Replayed: true`, body identical to the original create's response (the order, not the retry's input - a same-key request with a *different* body is not validated against the original; this lab treats the key alone as the identity, matching the simplest real-world implementations rather than also hashing/comparing the payload).
- Storage: `CachedOrder` (the same flattened DTO the read-cache already uses) under `orders:idempotency:{key}` in Redis, TTL 24h - long enough to cover realistic client retry windows, short enough not to accumulate indefinitely.

## What didn't work (three separate, real infrastructure findings)

Getting this validated live in K3s surfaced three genuine bugs unrelated to the application code itself - the code was correct from the first deploy attempt (confirmed by running the built image standalone via `docker run`, bypassing K3s entirely, before any of the following was diagnosed).

**1. `ctr` without an explicit socket address talks to the wrong containerd.** This host runs Docker (which brings its own containerd) *and* K3s (which runs its own separate embedded containerd at `/run/k3s/containerd/containerd.sock`). Every `docker save <image> | sudo ctr -n k8s.io images import -` command used throughout this lab's history was importing into whichever containerd `ctr` defaults to when no `--address`/`CONTAINERD_ADDRESS` is given - not necessarily K3s's. `sudo k3s crictl images` (the CRI view kubelet actually queries) proved this directly: it never listed the freshly-imported tag, even though `sudo ctr -n k8s.io images ls` reported it as present and "complete". **Fix: use `sudo k3s ctr -n k8s.io images import -` (K3s's own wrapper, which points at the right socket) for any image kubelet needs to see.** This had apparently been silently working around itself in every earlier milestone in this lab's history by coincidence (or been masked by `imagePullPolicy` differences) - this is the first time it was caught directly.

**2. Reusing a floating image tag (`milestone-7`) does not reliably invalidate what kubelet considers cached, even after the underlying image content changes.** Independent of finding #1: after fixing the socket issue, the same tag string still didn't pick up new content reliably - `ctr images rm` + reimport + force-deleting pods still left the old binary running (confirmed by comparing the running container's `Orders.Api.dll` SHA-256 against the known-good build's hash). Moving to a unique, never-before-used tag per build (`milestone-32-idempotency`) resolved it immediately and unambiguously. This is the standard reason real CI/CD pipelines mint a unique tag per build rather than reusing one - this lab had been getting away without that discipline until a same-tag rebuild's *behavioral* difference (not just a version bump) actually needed to be observed.

**3. Argo CD's `PreSync` migration-job hook has a 180-second `activeDeadlineSeconds` that can legitimately be exceeded by an unrelated transient condition, and a hook failure blocks the entire sync - including resources with no relation to the hook.** While finding #1 was still unresolved, the `orders-migrations-m7` hook Job failed with `ErrImageNeverPull` (the tag literally wasn't visible to kubelet yet) and then again after a retry attempt overlapped with a live LVM filesystem resize on the node (`lvextend`/`resize2fs` on the root volume, done in parallel to free up space) - both legitimate transient conditions, but Argo CD's `automated: {selfHeal: true}` sync policy retried and failed the *whole* application sync (leaving the unrelated `orders-api` `Rollout` change unapplied) rather than just the hook. No code change needed here - just root-causing it correctly required inspecting `.status.operationState` per-resource, not just the top-level `OutOfSync`/`Failed` status.

## Results

Live validation against the real K3s `Service` ClusterIP, post-fix:

```
$ curl -X POST http://$SERVICE_IP/orders -H "Idempotency-Key: final-validation-..." ...
HTTP/1.1 201 Created
{"id":"6cdcb0a9-...", "status":"Created", ...}

$ curl -X POST http://$SERVICE_IP/orders -H "Idempotency-Key: final-validation-..." ...   # same key, same body
HTTP/1.1 200 OK
idempotency-replayed: true
{"id":"6cdcb0a9-...", "status":"Created", ...}   # identical order, not a new one

$ curl -X POST http://$SERVICE_IP/orders ...   # no key
HTTP/1.1 201 Created
{"id":"f1f848b4-...", ...}   # a genuinely new, different order
```

### Regression check

`dotnet test`: 27 unit tests (3 new: `CreateOrderHandlerTests`), 2 new Redis integration tests (`RedisIdempotencyStoreTests`, against a real Testcontainers Redis) - all passing. `k6-run.sh smoke` post-deploy: `failed_rate=0`, `checks_rate=1`, `flow_rate=1`, `create_p95_ms` in the single digits once the process's connections are warm. The `get-order` p95 threshold (`<500ms`) was crossed on the first few post-deploy runs - root-caused to a small-sample-size statistical artifact, not a real regression: `smoke` runs only 1 VU for 10s (9-10 samples total), and each fresh `k6 run` process pays its own one-time connection-establishment cost on its first request (independently confirmed: manual sequential `curl` calls against fresh order IDs measured 1722ms → 270ms → 11ms → 10ms → 14ms - the *server* settles in milliseconds, but with n=9 the p95 statistic is dominated by that single first-request outlier). Not present in read-path code changed by this feature (`GetOrderHandler`/`RedisOrderCache` were untouched).

## Running it

```bash
curl -X POST http://<orders-api>/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: <client-generated-unique-key>" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"customerId":"...","items":[{"sku":"SKU-BOOK-002","quantity":1}]}'
```
