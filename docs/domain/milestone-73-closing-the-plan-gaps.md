# Milestone 73: Closing the Gaps the Plan Left Open

Milestones 67–72 implemented a six-phase plan. Auditing the result against the plan turned up six items that were never built, plus one inconsistency the plan never mentioned. This milestone closes five of them and argues explicitly against the other two.

## 1. Risk signals that Milestone 71 built the data for and never used

Phase 5 of the plan asked for two new payment risk signals: account age and address mismatch. It built exactly the data they need — `Customer.CreatedAt`, `ShippingAddress` — and then left `PaymentRiskEvaluator` with the same four signals Milestone 66 gave it. The comment in `Customer.cs` even claims the record "is what makes AccountAge a real signal." It was not, until now.

### NEW_ACCOUNT is not FIRST_PURCHASE

`FIRST_PURCHASE` fires on the absence of any history. By the second order it has stopped firing, which is exactly when the interesting pattern starts: an account that appeared eleven minutes ago and is already placing another order looks nothing like a returning shopper. `NEW_ACCOUNT` (+25) fires when the customer's *earliest* payment is inside a configurable window.

Derived from Payments' own history rather than carried from `Customer.CreatedAt`. Payments must be able to score a decision without a synchronous read into another service's database, and its own record of when it first saw this customer answers the question well enough.

### ADDRESS_MISMATCH needed the address to get there

This one could not be derived locally — Payments had no idea where anything shipped. The postal prefix now travels with the order: **Avro v4** on `OrderCreated` (default `""`), a matching field on `PaymentDecisionRequested` for the orchestrated path, and a column on `payments` so the next order has something to compare against.

The prefix rather than the address: it is the coarsest thing that still answers the question, and Payments has no business holding a street address it would only ever compare for equality.

Absence is scored as **unknown, not mismatched**. A customer with no shipping history, or an order with no address, produces no signal. Scoring absence as mismatch would have flagged every order placed before Milestone 71 — which was all of them, and is how a signal becomes noise nobody acts on.

At +20 it never declines on its own. People move and send gifts.

### An interaction that broke an existing test, correctly

`RapidRepeatPurchasesTripTheVelocitySignal` started failing at 60 instead of 35. The three seeded payments were all within the last three minutes, which makes the account three minutes old — so `VELOCITY(35)` and `NEW_ACCOUNT(25)` both fired and crossed the decline threshold.

That is the right answer. The test wanted to isolate `VELOCITY`, so it now seeds an older payment to establish the account, and the compounding it exposed is pinned in its own test. Observed on the lab server:

```
riskScore=45 signals=[NEW_ACCOUNT(+25); ADDRESS_MISMATCH(+20)]   → approved
```

Two signals, neither fatal, on an order shipping to Pará from an account that had only ever shipped to São Paulo.

## 2. Boleto

The plan said "Pix/Cartão/Boleto." Only two existed.

A boleto is a printed slip the shopper pays at a bank, days later or never. It is two-phase like a card in that the money has not moved yet — but for the opposite reason. A card authorization is a bank holding someone's funds; a boleto holds nothing at all. So it does **not** share `Authorized`:

```
Authorized      ──capture──► Captured    (Card: the hold becomes a charge)
AwaitingPayment ──capture──► Captured    (Boleto: the shopper paid the slip)
                ──expire───► Expired     (the due date passed unpaid)
```

Calling an unpaid boleto "authorized" would claim a guarantee that does not exist. Both states settle through the same commands, so every guard asks `IsAwaitingSettlement` rather than naming one state and quietly excluding the other — which is precisely the bug that shape invites.

The expiry sweeper needed one change (`state IN (Authorized, AwaitingPayment)`) and one rename: `"authorization window elapsed without capture"` was accurate for a card and wrong for a boleto, which has no authorization to elapse.

Windows are per method now — 30 minutes for a card hold, 120 for a boleto. Both are lab-scale compressions of days.

Observed on the server:

| method | state |
| --- | --- |
| Boleto | `AwaitingPayment` |
| Card | `Authorized` |

and `paymentMethod: "Cheque"` returns 400.

## 3. The reorder point stopped being decorative

`ReorderPoint` and `NeedsReplenishment` shipped in Milestone 72 and nothing read them. The model carried a number that implied stock gets replenished, and then never acted on it.

`WarehouseReplenishmentNeeded` now goes out through Inventory's existing outbox, **in the same transaction as the reservation that caused it** — a rollback cannot leave a replenishment alert for stock that was never drawn down.

Emitted on the **crossing**, not on every reservation that finds a warehouse already low. Verified on the server: the order that took WH-SP from 5 to 3 (reorder point 4) emitted one event; the next order, taking it from 3 to 2, emitted none.

```json
{"sku":"SKU-CLTH-001","warehouseCode":"WH-SP","availableQuantity":3,"reorderPoint":4,...}
```

Nothing consumes this yet, and that is the honest state of it: the event is emitted durably, and the replenishment process is somebody else's milestone.

### The new topic opened the circuit breaker

The first attempt published nothing. `KAFKA_AUTO_CREATE_TOPICS_ENABLE=false` in Compose, so producing to a topic nobody declared timed out — and the timeout tripped the **shared** `kafka-producer` circuit breaker, which then rejected every publish from inventory-service, not just this one.

Same shape as Milestone 69's missing DI registration: one new thing wired in wrong, and the entire outbox stops, with the service reporting healthy throughout. Topics in Compose are declared explicitly (Kubernetes leaves auto-create on), and the declaration was the missing half of adding a topic.

## 4. Single-sweeper, without a Kubernetes Lease

The plan wanted `PaymentAuthorizationSweeper` gated on `LeaderElectionService`, the way `SagaTimeoutSweeper` is. Milestone 68 skipped it and argued that `FOR UPDATE SKIP LOCKED` already makes concurrent sweeps safe — which is true, and is also what the leader-election comment in `SagaOrchestrationStore` says about its own case.

What SKIP LOCKED does not prevent is every replica *polling* every tick. That is the part worth winning, and a Kubernetes Lease is an expensive way to win it: moving `LeaderElectionService` out of Orders.Worker, granting Payments RBAC on Lease objects, and setting `automountServiceAccountToken: true` on a pod where Milestone 26 deliberately turned it off. Real security surface, to gate a loop whose correctness never depended on it.

`pg_try_advisory_xact_lock` buys exactly the missing piece and nothing more. It never waits — a replica that does not get the lock skips the tick instead of queueing — it is transaction-scoped so there is no unlock call to miss, and it behaves identically under Compose and Kubernetes.

## 5. Startup validation, everywhere this time

Milestone 69 added `ValidateOnBuild`/`ValidateScopes` after one unregistered `IProducer` took down the whole outbox while the service reported healthy. It went into three services. Five did not get it — including Inventory.Service, which is where Milestone 72 had just added a new dependency.

The guard exists because a background loop cannot fail loudly on its own. Leaving it off the service that just grew a new registration is the worst possible place to leave it. All eight services now refuse to start rather than limp.

All 24 containers came up after the change, so there were no latent gaps to find — which is the outcome you want and not one you can assume.

## What was deliberately not done

**Partial fulfilment (Phase 6).** The plan asked for it; Milestone 72 implemented all-or-nothing instead. A partial reservation confirms an order the warehouse cannot fill, and the missing units need somewhere to wait — a backorder state, and a replenishment loop to release them. `WarehouseReplenishmentNeeded` is the first half of that; the rest is a milestone, not a gap.

**A `Paid` order state (Phase 3).** The plan listed `Created → Confirmed → Paid → Picking`. With authorize/capture, the money moves at `Shipped` — that is where capture runs. A `Paid` state sitting before `Picking` would assert something false about where the money is. The state machine is right and the plan was written before Milestone 68 existed.

## See also

- [Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk](milestone-66-line-items-pricing-and-risk.md) — the four original risk signals.
- [Milestone 68: Authorize, Then Capture](milestone-68-authorize-capture.md) — the payment state machine Boleto extends.
- [Milestone 71: The Customer Stops Being a String](milestone-71-customers-tiers-and-geography.md) — where the address that feeds ADDRESS_MISMATCH comes from.
- [Milestone 72: Stock Lives in Buildings](milestone-72-multi-warehouse-allocation.md) — where the reorder point came from.
