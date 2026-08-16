# Loose Ends, Business Rules and DevOps Review — Implementation Plan (2026-08-16)

Eighth audit in the current series. The seven before it worked outward from
different centres:

| Pass | Centre | Outcome |
|---|---|---|
| [08-14 service and business rules](audit-2026-08-14-service-and-business-rule-review.md) | the saga's seams | 13 findings, closed in `84cd877` |
| [08-15 frontend/catalog/infra](audit-2026-08-15-frontend-catalog-infra-review.md) | the deployment layer | closed |
| [08-15 architecture and cross-cutting](audit-2026-08-15-architecture-and-cross-cutting-review.md) | shared building blocks, config, alerting | closed in `6118979` |
| [08-15 build/runtime/time](audit-2026-08-15-build-runtime-and-time-handling-review.md) | the container and the clock | closed in `f839dd5` |
| [08-15 domain and business rules](audit-2026-08-15-domain-and-business-rules-review.md) | the money | closed in `8ce17ec` |
| [08-15 patterns and API layer](audit-2026-08-15-patterns-and-api-layer-review.md) | ports/adapters, gRPC, pagination, rate limiting, test infra | closed in `567dc02` |
| [roadmap 91-99](../roadmap-milestones-91-99.md) | the saga as a distributed system | closed in `77feb39` |

This pass reads the system along three axes none of the seven fully closed:

1. **Whether the M91-99 resilience fix actually reached every sibling that
   needed it**, the same "correct in one place, skipped in the next"
   question the patterns-and-API-layer pass asked of an older change.
2. **Whether the one business-rule gap this series has known about since
   the Milestone 81-90 roadmap is still open.** That roadmap's own words:
   "Phase 5 (Milestone 90) was not attempted." Nobody has re-checked since.
3. **DevOps practice as its own subject.** The frontend/catalog/infra pass
   touched deployment concerns in passing; this is the first pass that
   reads CI/CD, Kubernetes, IaC, and observability configuration as the
   primary target rather than a side effect of a code change.

**Method.** Three independent passes, each anchored in what the prior
audits already closed (so nothing below re-flags fixed ground) and in this
repo's own stated intent — milestone docs, roadmap documents, `AGENTS.md`
— checked against the current code and config rather than against itself.
Every finding below was spot-checked against the live repository before
being written down.

---

## Executive summary

| # | Finding | Severity | Theme |
|---|---|---|---|
| 1 | Milestone 90 (promotion calendar, exclusivity, budget) — still never built | P1 | Business rules |
| 2 | The replenishment loop can restock the wrong warehouse, silently, forever | P1 | Business rules |
| 3 | The new resilience pipeline reached `Orders.Worker` only — Payments/Inventory transaction writers run on a bare 30s timeout | P1 | Cross-service |
| 4 | Runtime-stage image floats on `10.0` despite a pinned build stage; no scheduled rescan | P1 | DevOps |
| 5 | No egress `NetworkPolicy` anywhere in the cluster | P1 | DevOps |
| 6 | Backup and chaos validation are 100% manual, never re-run | P1 | DevOps |
| 7 | The BFF's actual checkout route bypasses its own header-forwarding fix | P2 | Cross-service |
| 8 | `outbox.dead_lettered` is emitted everywhere, alerted on nowhere | P2 | Cross-service |
| 9 | Ansible Helm releases have no `chart_version` pin | P2 | DevOps |
| 10 | Chaos/backup drills have no runbook tying results back to alert validation | P2 | DevOps |
| 11 | `payments-service`/`orders-worker` have no PodDisruptionBudget, undocumented | P3 | DevOps |
| 12 | Admin UIs (Grafana, Prometheus, Alertmanager, kafka-ui) sit behind no auth layer beyond LAN scoping | P3 | DevOps |

---

## 1. Milestone 90 — promotion calendar, exclusivity, and budget — still never built (P1)

`docs/roadmap-milestones-81-90.md:17` says it plainly: "Phase 5 (Milestone
90) was not attempted." `docs/roadmap-milestones-91-99.md` confirms it was
left "unclaimed" a second time, deliberately, because that audit "reads
the same system as a distributed system" and does not re-examine business
rules. As of today, `grep -rniE "exclusiv|budget" apps/src/Orders.Domain
apps/src/Orders.Application/Pricing` returns **zero hits**. The gap is
still open.

**What exists today** is more than the roadmap implies for coupons
specifically: `Orders.Domain/Coupon.cs:24-26,44-66,124-182` has
`ValidFrom`/`ValidUntil`, `MinimumOrderAmount`, `MaxTotalRedemptions`,
`MaxPerCustomer`, and an atomic reservation-based claim (`CouponRedemption`),
evaluated in `OrderPricingService.ResolveCouponAsync`
(`Orders.Application/UseCases/CreateOrder/OrderPricingService.cs:152-164`)
against `timeProvider.GetUtcNow()`.

**What is still missing**, confirmed by reading
`Orders.Application/Pricing/PromotionRules.cs` end to end:

- `CategoryDiscountRule` (55-98), `BulkQuantityRule` (104-130),
  `LoyaltyTierRule` (137-161), and `FreeShippingRule` (169-184) have **no
  validity window at all** — they fire purely off `PricingOptions` config
  and live order/customer facts. `PricingOptions.cs` has no date field
  anywhere.
- **No exclusivity groups exist.** All four automatic rules plus the
  coupon rule stack unconditionally. `NRulesPricingEngine.Price`
  (`NRulesPricingEngine.cs:34-100`) sums every fired rule's
  `AppliedDiscount` and caps only the *total* at the subtotal
  (`discountTotal = rawDiscountTotal > subtotal ? subtotal :
  rawDiscountTotal`, line 59) — there is no "best of group wins"
  selection anywhere. A Gold-tier customer buying 5+ units of one
  electronics SKU with a coupon stacks TIER (7%) + CATEGORY (5%) + BULK
  (8%) + COUPON, uncapped by anything but the subtotal floor.
- **No campaign budget exists.** `Coupon.MaxTotalRedemptions` is a *count*
  limit, not a depleting monetary budget. There is no field anywhere —
  coupon or promotion — for "this campaign may give away at most R$X
  total," and consequently no analogue of the atomic last-slot claim
  `CouponRedemptionStore` already solved for redemption counts
  (`Coupon.cs:36-42`) exists for money.

**Minor, not a separate defect:** coupon validity is checked against wall
clock `timeProvider.GetUtcNow()` at pricing time rather than literally
`order.CreatedAt`. In practice these coincide, since pricing happens
synchronously inside order creation — worth a note only if pricing is ever
made async or `CreatedAt` is ever backdated.

**Fix**, matching the roadmap's own shape:

1. Add `ValidFrom`/`ValidUntil` to a `Promotion` config record replacing
   the raw `PricingOptions` dictionaries (or add the fields per rule) and
   check them the same way `ResolveCouponAsync` already checks coupon
   dates.
2. Add a `PromotionGroup` fact type, inserted per `AppliedDiscount`, and a
   best-of-group reducer alongside `NRulesPricingEngine.CapDiscounts` that
   keeps only the highest-value discount per group before summing.
3. Add a `BudgetRemaining`-style column with the same atomic
   guarded-`UPDATE` pattern `CouponRedemptionStore` uses for redemption
   counts — reserved at claim time, released on cancellation, exactly the
   way coupon slots already work.

---

## 2. The replenishment loop can restock the wrong warehouse, silently, forever (P1)

`Inventory.Service`'s multi-warehouse network has **two uncoordinated
warehouse-selection rules**, and the newer one — the M89 replenishment
loop — silently discards the information needed to target the right
warehouse.

**Reservations** draw from a fixed priority order:
`WarehousePriority` (`WarehouseAllocationStore.cs:315-320`) ranks `WH-SP`
= 1, `WH-RJ` = 2, everything else = 9; `StockAllocator.Allocate`
(`Domain/StockAllocation.cs:157-161`) draws from the highest-priority
warehouse that can cover the order.

**Restocks** ignore priority and pick the warehouse with the **least**
available stock:

```csharp
// WarehouseAllocationStore.cs:269-278
/// this lab is a stand-in for "wherever the returns depot routes them" -
/// a real network decides from the return label, not the stock levels.
public async Task<bool> TryRestockAsync(string sku, int quantity, ...)
{
    var stocks = await dbContext.WarehouseStocks
        .Where(item => item.Sku == sku)
        .OrderBy(item => item.AvailableQuantity)
        .ToListAsync(cancellationToken);
```

A separate doc comment 40 lines above this method
(`WarehouseAllocationStore.cs:229-231`) states the opposite intent: *"…
`TryRestockAsync` itself picks whichever warehouse has the most room."*
Ascending `OrderBy` followed by taking the first entry cannot select "the
most room" — the code and its own neighboring comment contradict each
other. That mismatch alone is survivable for the *returns* path this
method was built for (the doc comment even hedges that the routing policy
is a stand-in). It becomes a serious, silent bug for the replenishment
loop:

- `WarehouseReplenishmentNeeded` carries a specific `WarehouseCode` — the
  warehouse that actually crossed its reorder point
  (`InventoryReservationMessageProcessor.cs:96-98,156-177`).
- `ReplenishmentRequestProcessor.ProcessAsync`
  (`ReplenishmentRequestProcessor.cs:84`) creates a `PurchaseOrder` that
  **does** carry that code (`Domain/PurchaseOrder.cs:24,41`), whose own
  doc comment states: "the stock it represents actually lands and
  **restocks the warehouse it was requested for**" (lines 11-12).
- But `PurchaseOrderReceivingSweeper.SweepAsync` builds the eventual
  restock command as:

  ```csharp
  // PurchaseOrderReceivingSweeper.cs:109-110
  var request = new InventoryRestockRequested(
      Guid.NewGuid(), purchaseOrder.Id, purchaseOrder.Sku, purchaseOrder.Quantity,
      purchaseOrder.CorrelationId, now);
  ```

  and `InventoryRestockRequested`
  (`BuildingBlocks.Contracts/ReturnContracts.cs:21-27`) **has no
  `WarehouseCode` field**:

  ```csharp
  public sealed record InventoryRestockRequested(
      Guid ReturnId, Guid OrderId, string Sku, int Quantity,
      string CorrelationId, DateTimeOffset RequestedAt);
  ```

  `purchaseOrder.WarehouseCode` is read for a log line one statement later
  and then discarded — it never reaches the mutation.
- The consuming handler
  (`InventoryReservationMessageProcessor.cs:344`) calls the same
  warehouse-agnostic `TryRestockAsync(sku, quantity, ...)`, which routes
  stock to whichever warehouse currently has the *least* available
  quantity for that SKU — not necessarily the one the purchase order was
  raised for.

**Concrete failure scenario**, using the real seeded topology
(`Data/Migrations/20260807114409_AddMultiWarehouseStock.cs:52-74` seeds
every SKU into both `WH-SP` and `WH-RJ`): SKU `X` has `WH-SP` at 2 units
(just crossed its reorder point of 5) and `WH-RJ` at 1 unit (already below
its own reorder point). `WarehouseReplenishmentNeeded` fires for `WH-SP`,
a purchase order targeting `WH-SP` is raised — and on receipt lands in
`WH-RJ` instead, because `WH-RJ`'s available quantity (1) is lower than
`WH-SP`'s (2). `WH-SP` stays under-stocked.

**This compounds because the signal is edge-triggered, not
level-triggered.** `TryApplyReservationAsync`
(`WarehouseAllocationStore.cs:83-99`) only fires
`WarehouseReplenishmentNeeded` on the transition into
`NeedsReplenishment`. Once `WH-SP` is already below its reorder point, no
further signal is emitted for it specifically — if its one purchase order
lands in the wrong warehouse, `WH-SP` can stay under-stocked
**indefinitely**, silently, while whichever warehouse happens to be lowest
at each receiving-sweep tick keeps absorbing purchase orders it never
generated a signal for.

**Fix.** Add `WarehouseCode` to `InventoryRestockRequested` /
`InventoryRestockReplied`, thread it from
`PurchaseOrderReceivingSweeper.cs:109` through a new warehouse-specific
overload of `TryRestockAsync` that restocks that exact `(sku,
warehouseCode)` row directly. For the pre-existing returns-path callers
where no specific warehouse is known, either fix the selection to match
its stated intent (`OrderByDescending`, not `OrderBy`) or restate the
comment to match whichever policy is actually chosen — "most room" and
"least room" are both defensible, but only one should be claimed.
Separately, consider making the reorder-point signal level-triggered (or
re-checked on a timer) so a warehouse that never got correctly
replenished eventually re-fires instead of going silent forever.

**Test.** Seed `WH-RJ` below `WH-SP` in available quantity, raise and
receive a purchase order explicitly targeting `WH-SP`, and assert the
stock landed in `WH-SP` — the scenario above, made deterministic.

---

## 3. The new resilience pipeline reached `Orders.Worker` only (P1)

`BuildingBlocks.Resilience/ResilienceExtensions.cs:41-90` introduces
`PostgresTransactionPipeline`, documented generically as being for
"CAS-guarded, multi-statement transactions … whose retry unit is the whole
lambda" — a codebase-wide pattern by its own doc comment. It was wired
into exactly three `Orders.Worker` classes:
`SagaOutboxPublisher.cs:54`, `OrderStatusStore.cs:80`,
`SagaOrchestrationStore.cs:88`.

`Payments.Service` and `Inventory.Service` have the structurally identical
shape — `BeginTransactionAsync` → CAS-guarded domain write → outbox insert
→ `CommitAsync` — in at least eight classes, and **none reference
`ResiliencePipeline`/`GetPipeline` at all**: `PaymentSettlementProcessor.cs:69`,
`PaymentAuthorizationSweeper.cs:70`, `PaymentDecisionRequestProcessor.cs`,
`PaymentMessageProcessor.cs`, `InventoryReservationMessageProcessor.cs:72,319`,
`BackorderTimeoutSweeper.cs:71`, `PurchaseOrderReceivingSweeper.cs`,
`ReplenishmentRequestProcessor.cs`. The shared `InboxStore
.TryRecordWithinTransactionAsync` helper these classes call is also a bare
`ExecuteSqlInterpolatedAsync`, no pipeline. Neither service overrides
`CommandTimeout` in its connection string or `Program.cs`, so these
transactions run on Npgsql's bare 30-second default with no circuit
breaker at all.

**Consequence.** A payment capture or inventory reservation transaction
can stall up to 30 seconds with nothing tripping a breaker — six times
`Orders.Worker`'s own 5-second timeout — and repeated Postgres slowness in
Payments/Inventory never opens a breaker to protect their readiness probes
the way it now does in `Orders.Worker`.

**Fix.** Inject `PostgresTransactionPipeline` into
`PaymentSettlementProcessor`, `PaymentAuthorizationSweeper`,
`PaymentDecisionRequestProcessor`, `PaymentMessageProcessor`,
`InventoryReservationMessageProcessor`, `BackorderTimeoutSweeper`,
`PurchaseOrderReceivingSweeper`, and `ReplenishmentRequestProcessor`, and
wrap each transaction body in `_pipeline.ExecuteAsync` the same way the
three `Orders.Worker` classes already do.

---

## 4. Runtime-stage image floats while the build stage is pinned (P1)

Every one of the seven service Dockerfiles pins its build stage exactly to
`global.json`:

```
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
...
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
```

Confirmed across all seven (`Cart`, `Catalog`, `Inventory`, `Orders.Api`,
`Orders.Worker`, `Payments.Service`, `Storefront.Service`). The image that
actually ships floats on whatever `10.0.x` Microsoft has published at
build time — one line below a pin that exists specifically for
reproducibility. There is also no scheduled rebuild: `.github/workflows
/ci.yml`'s Trivy scan only runs when a push touches `main`, unlike
`codeql.yml`, which explicitly runs on a weekly cron "to catch new
query-pack rules against unchanged code" — the same reasoning never
applied to the base image. No Dependabot/Renovate config exists anywhere
to catch this by PR either.

**Fix.** Pin the runtime stage to a specific patch or digest
(`aspnet:10.0.0` or `aspnet@sha256:...`), and add a weekly scheduled CI
job that rebuilds and rescans every image even with no source changes,
mirroring `codeql.yml`'s own stated rationale for its cron.

---

## 5. No egress `NetworkPolicy` anywhere in the cluster (P1)

`kubernetes/base/network-policies.yaml` has exactly two policies,
`default-deny-ingress` and `allow-health-and-api`, and both set
`policyTypes: [Ingress]` only. A repo-wide `grep -r Egress
kubernetes/` returns nothing. Every pod has unrestricted outbound access —
a compromised pod (a dependency supply-chain issue, an RCE in any of the
seven app images) can reach anything outbound, including the open
internet.

**Fix.** Add a `default-deny-egress` policy plus an explicit allow-list:
DNS, the specific infra endpoints each service already declares in
`infrastructure-endpoints.yaml`, and the OTLP collector.

---

## 6. Backup and chaos validation are 100% manual, never re-run (P1)

`docs/data-platform/milestone-80-nosql-backup-restore.md` says it
outright: "No continuous backup, no scheduled/automated drill." All three
data stores — Postgres (via CNPG), MongoDB, Redis — have been proven
restorable exactly once, by hand, at the milestone that built the drill
(`scripts/backup-drills/{mongodb,redis}-backup-restore-drill.sh`,
`kubernetes/data-platform/postgres-ha-backup.yaml`, a one-off `kind:
Backup`, not a `ScheduledBackup`). All four
`kubernetes/chaos-experiments/*.yaml` manifests are each explicitly
commented "Applied ad hoc for the game day, not left running." Nothing
re-verifies any of this. The repo already has the right pattern for
exactly this class of problem — `.github/workflows/mutation-testing.yml`
and `coupon-redemption-concurrency.yml` both run on a weekly cron
specifically because they're "too slow/heavy for every push" — it was
just never extended to backup drills or chaos game days. A regression in
`mongodump`/`BGSAVE`/CNPG WAL archiving introduced by a future infra
change would not be caught until an actual incident forces a real
restore.

**Fix.** Add a scheduled-cron workflow, same shape as
`memory-leak-check.yml` (self-hosted runner), that runs the backup drills
monthly and at least one chaos game day quarterly, alerting on failure.

---

## 7. The BFF's actual checkout route bypasses its own header-forwarding fix (P2)

`Storefront.Service/ProxyEndpoints.cs` gained
`ForwardedRequestHeaders`/`CopyForwardedRequestHeaders` (lines 35,
161-171) specifically to stop `X-Correlation-ID`/`Idempotency-Key`/
`Accept` from being silently dropped in the M91-99 fix. But
`StorefrontEndpoints.CheckoutAsync` — mapped at
`/api/storefront/checkout`, the one route that actually creates orders
and moves money — builds its three outbound `HttpRequestMessage`s by hand
(`PostOrderAsync:301-331`, the cart GET at `208-234`, the cart clear at
`279-286`) and only ever sets `Authorization` and its own computed
`Idempotency-Key`. It never calls `CopyForwardedRequestHeaders`, so a
browser-supplied `X-Correlation-ID` is dropped precisely on the
highest-value path, while the same header now survives on the generic
`/api/orders` passthrough and `/api/cart/*` proxy routes the fix
targeted. Cross-service log correlation for the real checkout flow is
worse than for the passthrough route the fix was written for.

**Fix.** Route `PostOrderAsync` and the two cart calls through
`CopyForwardedRequestHeaders` (excluding `Idempotency-Key`, which
`CheckoutAsync` deliberately computes and overrides itself).

---

## 8. `outbox.dead_lettered` is emitted everywhere, alerted on nowhere (P2)

`OrdersTelemetry.RecordOutboxDeadLettered` is called consistently from
the shared `OutboxPublisher<TDbContext>.ApplyPublishAttemptAsync` (used by
Orders, Payments, and Inventory) and from
`SagaOutboxPublisher.MoveToDeadLetterAsync:266` — genuinely uniform
coverage, not a gap in the code. But
`observability/prometheus/rules/messaging-ops.yml` has no rule referencing
`outbox_dead_lettered_total` anywhere; only the pre-existing
`messaging_dead_letters_total` (the Kafka-topic-level DLQ) and
`outbox_messages_pending` backlog alert exist. The M91-99 roadmap's own
Phase 2 explicitly asked to "alert on it separately from backlog growth" —
that half was never done, for any service. A poison outbox row now
correctly stops retrying and gets dead-lettered instead of retrying
forever, but nothing pages anyone when it happens.

**Fix.** Add an `OutboxDeadLettered` rule
(`increase(outbox_dead_lettered_total[5m]) > 0`) alongside the existing
`DeadLetterMessagesDetected`, same zero-tolerance shape.

---

## 9. Ansible Helm releases have no `chart_version` pin (P2)

`iac/ansible/roles/k3s/tasks/main.yml:8-12` pins `k3s_version:
v1.36.2+k3s1` with an explicit comment: "Pinned to the version this
cluster actually runs… an unpinned install script is not a reproducible
one." Two files over, `iac/ansible/group_vars/all.yml:4-34` lists six
`helm_releases` entries (sealed-secrets, argo-rollouts, argocd, keda,
kyverno, cnpg) with **no `chart_version` key on any of them**
(`iac/ansible/roles/cluster-addons/tasks/main.yml:11-20`), so every
playbook run installs or upgrades to whatever is newest in each chart
repo at that moment — exactly the non-reproducibility the K3s role's own
comment warns against, one layer up. The Linkerd install
(`roles/cluster-addons/tasks/main.yml:33-69`) has the same gap: no CLI or
chart version pin, relying on whatever `linkerd` happens to be on the
host's `$PATH`.

**Fix.** Add `chart_version:` to every `helm_releases` entry and
re-verify the cluster after bumping; pin the Linkerd CLI to the version
this cluster was actually built with.

---

## 10. Chaos/backup drills have no runbook tying results to alert validation (P2)

Related to Finding 6, and distinct enough to fix separately.
`correctness-invariants.yml`'s own header already says its thresholds are
"not yet calibrated… each should be proven to actually fire using the
drill/chaos script noted per alert before being trusted as a real page."
Several `severity: page` alerts (`AntiEntropyDivergenceDetected`,
`SettlementReconciliationUnresolved`, `RateLimitingFailedOpen`) are
annotated with which script should provoke them, but nothing tracks
whether that provocation has actually happened and confirmed the alert
fires as designed.

**Fix.** A short runbook table (matching the style already used for
M16/M79's milestone docs) mapping each `severity: page`
correctness-invariant alert to its provocation script and a
last-confirmed-fired date.

---

## 11. `payments-service`/`orders-worker` have no PodDisruptionBudget, undocumented (P3)

Unlike `cart-service.yaml`, `storefront-service.yaml`,
`catalog-service.yaml`, `orders-api.yaml`, and `inventory-service.yaml`
(all of which define one), `kubernetes/base/payments-service.yaml` and
`orders-worker.yaml` have none. Likely intentional in both cases —
`payments-service` runs `replicas: 1` with `strategy: Recreate`, where a
PDB with `minAvailable: 1` would block all voluntary node drains; `orders
-worker`'s replica count is owned by its KEDA `ScaledObject`, not the base
manifest. The omission is structurally defensible but undocumented —
nothing states it's deliberate the way, e.g., `orders-runtime-sealed
-secret.yaml` documents its own choices.

**Fix.** A one-line comment in both manifests stating why no PDB, so a
future contributor doesn't "fix" the omission by adding one that then
blocks node drains.

---

## 12. Admin UIs sit behind no auth layer beyond LAN scoping (P3)

`compose/nginx/domains.conf` proxies all six `server` blocks — storefront,
Keycloak, orders, Grafana, kafka-ui, Prometheus — straight through with no
`auth_basic` and no OAuth gate. This is LAN-only by design (requires
`/etc/hosts` entries, no public DNS), and
`docs/slo/milestone-16-slo-burn-rate-alerting.md` already notes Grafana
runs with "anonymous viewer already enabled." `AGENTS.md`'s own stated
principle is "Keep PostgreSQL, Kafka, and administrative interfaces off
public interfaces" — kafka-ui (a Kafka admin UI) and Prometheus/
Alertmanager (which can leak internal topology and cardinality) sitting on
the same unauthenticated HTTPS surface as the customer-facing storefront
is a soft violation of that intent, even where LAN-scoped exposure is a
reasonable trade-off for a personal lab.

**Fix.** Either gate kafka-ui/Prometheus/Alertmanager behind
`auth_basic` in `domains.conf`, or add a one-line comment there
acknowledging the accepted risk, the way other deliberate trade-offs in
this repo are documented.

---

## Implementation plan

### Phase 1 — Stop the silent under-stock (1 session)

Finding 2. Highest urgency: a live, unbounded correctness bug that can
leave a real warehouse under-stocked indefinitely with no further signal.

1. Add `WarehouseCode` to `InventoryRestockRequested`/
   `InventoryRestockReplied`.
2. Thread `purchaseOrder.WarehouseCode` from
   `PurchaseOrderReceivingSweeper` into a new warehouse-specific
   `TryRestockAsync` overload that restocks the exact `(sku,
   warehouseCode)` row.
3. Fix or restate the returns-path fallback's selection policy
   (`OrderBy` → `OrderByDescending`, or update the comment to match).
4. Test: seed `WH-RJ` lower than `WH-SP`, raise and receive a purchase
   order targeting `WH-SP`, assert the stock landed in `WH-SP`.

**Done when:** the scenario in Finding 2 is a passing regression test, and
a purchase order always restocks the warehouse it was raised for.

### Phase 2 — Extend the resilience pipeline to Payments and Inventory (1 session)

Finding 3.

1. Inject `PostgresTransactionPipeline` into the eight listed classes.
2. Wrap each transaction body in `_pipeline.ExecuteAsync`, matching the
   three `Orders.Worker` classes exactly.
3. Extend `InboxStore.TryRecordWithinTransactionAsync` the same way.

**Done when:** stopping Postgres mid-transaction on a Payments or
Inventory writer produces the same 5-second timeout and breaker behavior
`Orders.Worker` already has, not a 30-second hang.

### Phase 3 — Close the two small consistency gaps (1 session)

Findings 7 and 8, both small and independent.

1. Route `CheckoutAsync`'s three outbound calls through
   `CopyForwardedRequestHeaders`.
2. Add the `OutboxDeadLettered` Prometheus rule.

**Done when:** a checkout request's `X-Correlation-ID` is traceable
end-to-end in Tempo, and a synthetic dead-lettered outbox row fires an
alert within 5 minutes.

### Phase 4 — Promotions get a calendar, a priority, and a budget (milestone-sized)

Finding 1 — the long-open Milestone 90. Larger than the other phases by
design; the roadmap already scoped it as its own milestone.

1. Validity windows on every promotion rule.
2. Exclusivity groups with a best-of-group reducer.
3. A depleting campaign budget using the same atomic claim pattern as
   coupon redemption slots.

**Done when:** two promotions in the same exclusivity group on one order
apply only the larger discount, and a depleted campaign budget declines
further redemptions atomically under concurrent load — mirroring the
existing coupon last-slot concurrency test.

### Phase 5 — Reproducibility and blast radius (1-2 sessions)

Findings 4, 5, and 9.

1. Pin the runtime-stage base image in all seven Dockerfiles; add a
   weekly rebuild+rescan CI job.
2. Add `default-deny-egress` plus an explicit allow-list NetworkPolicy.
3. Pin `chart_version` on all six Ansible `helm_releases` entries and the
   Linkerd CLI version.

**Done when:** no Dockerfile `FROM` line lacks a full version, `kubectl
get networkpolicy` shows an egress deny-plus-allowlist pair, and a second
`ansible-playbook --check` run diffs net-zero.

### Phase 6 — Make backup and chaos validation self-proving (1 session)

Findings 6, 10, 11, 12.

1. Scheduled monthly backup-drill and quarterly chaos-game-day CI jobs
   (self-hosted runner, same shape as `memory-leak-check.yml`).
2. The alert-provocation runbook table.
3. One-line comments documenting the PDB omission on `payments-service`
   and `orders-worker`.
4. A decision, recorded in `domains.conf`, on admin-UI auth.

**Done when:** a monthly CI run posts pass/fail for each data store's
restore drill, and the correctness-invariants alert table has no
provocation cell older than one quarter.

---

## What is genuinely solid

Checked deliberately, held up, and worth recording so a later pass does
not re-audit it:

- **Everything the closed `8ce17ec` domain audit fixed still holds.**
  `Customer.ReverseCompletedOrder` is wired from both cancellation and
  full-return paths; `DiscountPriority`/`CapDiscounts` pays out
  shopper-presented discounts before automatic ones when the cap binds;
  the backorder FIFO queue and risk-query bounding fixes remain in place.
- **Milestone 82's tax/shipping return proration is real.** `Order
  .TryReturn` reads `LineTax` per line and applies
  `ShippingRefundPolicy.IsOwed` keyed on return-reason category and a
  configurable regret window, matching the milestone spec exactly.
- **Payment risk scoring genuinely gates capture, with no gap.**
  `PaymentDecisionCoordinator.GetOrCreateAsync` evaluates risk once per
  order under a transaction-scoped advisory lock, and the approval flag is
  the sole input to `Payment.Authorize`. `TryCapture`/`TryRefund`/
  `TryCancel` are all idempotent state-guarded no-ops on redelivery —
  double-capture is not reachable.
- **The order lifecycle state table is coherent.**
  `OrderStatuses.AllowedPredecessors` centralizes every transition used by
  both the saga and the operator/self-service paths; `SettlementActionFor`
  centralizes the money-side effect per target status so Worker and Api
  cannot drift.
- **Reservation-side warehouse selection is the mirror image of Finding
  2, and it's correct.** `StockAllocator.Allocate` is pure, deterministic,
  and used identically by both normal checkout reservation and backorder
  release — the inconsistency is specifically in the *restock* direction.
- **DLQ topic configuration and dead-letter migrations are uniform** across
  every service that has an outbox (Orders, Payments, Inventory); Cart,
  Catalog, and Storefront have no outbox and are correctly out of scope.
  All eight DLQ topics moved from 1→3 partitions and 1-day→30-day
  retention with no stragglers.
- **`KafkaConsumerHost`/`KafkaHealthCheck` are used by every live
  consumer** in the system; only `DlqRedriveTool`, a manual CLI, hand-rolls
  its own consumer, correctly out of scope.
- **CI/CD is genuinely mature.** `ci.yml` gates merges on the whole
  `.slnx` — architecture-fitness and integration tests included, not just
  unit tests — plus gitleaks, automated `dotnet list package
  --vulnerable` CVE gating, cyclomatic-complexity and module-size budgets,
  and a coverage floor. The image pipeline is build → SBOM → Trivy
  CRITICAL/HIGH scan → cosign keyless signing → only then retag `:latest`.
  `verify-config-parity.sh` closes the exact config-drift P0 the
  frontend/catalog/infra audit flagged, enforced by CI rather than memory.
- **Dockerfiles** are consistent multi-stage, non-root (`USER $APP_UID`),
  with a `HEALTHCHECK` on every service and a matching SDK version across
  all seven and `global.json` — the runtime-stage float in Finding 4 is
  the one real gap.
- **Kubernetes workloads carry resource requests/limits and all three
  probe types**, consistently, on every app Deployment. The one Secret in
  the repo is a proper Bitnami `SealedSecret`, not plaintext. RBAC for
  `orders-worker` is scoped to exactly one verb-set on one resource in its
  own namespace — no cluster-admin bindings anywhere.
- **GitOps and supply-chain controls are both genuinely defense-in-depth.**
  Argo CD runs `automated: {prune: true, selfHeal: true}` and correctly
  `ignoreDifferences`s HPA/KEDA-managed replica counts; Kyverno enforces
  cosign signature verification pinned to the exact GitHub OIDC subject
  plus a require-digest rule.
- **The Kafka-topic-level DLQ already has a zero-tolerance alert**
  (`DeadLetterMessagesDetected`, fires on first occurrence) — Finding 8 is
  specifically about the newer, outbox-table-level dead-letter path, which
  is a different signal that was never given the same treatment.
