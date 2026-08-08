# Milestone 88: Divergence Between Services Stops Being Silent

## Scope

Milestone 76 built exactly one reconciliation: a settlement reply from Payments driving an order to `FulfillmentHold` when a capture couldn't happen. That is reactive - it fires when a specific message arrives. Nothing in this system periodically asks, independent of any message, "do these two services' records of the same order still agree?" Milestone 81's audit found three divergences of exactly that shape by reading code; this milestone builds the mechanism that would have found the same class of bug by *watching the system run*, and keeps watching for the next one.

Two invariants, chosen because they are the two that map directly onto real money and stock, not because they're the only ones that could exist:

1. Every order sitting in `Confirmed`, `Picking`, `Shipped`, or `FulfillmentHold` has committed to a payment - Payments' own record for that order must not be missing, and must not be `Declined` (a declined payment should never have let the order past `Created`).
2. Every backorder row belongs to an order that is still, actually, `Backordered` - not `Cancelled`, not anything else. A backorder surviving past its order's `Cancelled` transition is precisely Milestone 81's finding 1.3, before that milestone's fix existed.

## Design

**The decision logic is pure, in `BuildingBlocks.Contracts`, and knows nothing about how the two facts it compares were obtained.** `AntiEntropyChecks.OrderIsMissingAnAccountedPayment(string? paymentState)` and `AntiEntropyChecks.BackorderBelongsToAnOrderNoLongerWaiting(string orderStatus)` are each a one-line function of exactly the inputs their name promises - unit-tested directly, no database, no HTTP call, no test double standing in for either, the same discipline `StockAllocator` and `ReturnRefundCalculator` already established for this codebase's other domain rules.

**`AntiEntropySweeper` (Orders.Worker) gathers the two facts each check needs and does nothing else clever with them.** Candidate orders come from a direct query against Orders' own Postgres (raw Npgsql, the same pattern `OrderStatusStore` already uses for its own reads); each order's payment state comes from a new, narrow `GET /payments/by-order/{orderId}` on Payments.Service - the one HTTP read that service now serves, where every other interaction with it is Kafka. Backorders come from a new `GET /inventory/backorders` on Inventory.Service. A divergence is logged and counted (`anti_entropy.divergences`, tagged by which check failed) and nothing more - **the sweep never auto-corrects.** Guessing which side is right and rewriting the other would repeat exactly the failure mode this milestone exists to catch; a human, or a specifically-reasoned compensation (Milestone 81's fix, for the one class this already has one) decides what a real divergence means. Making it visible in minutes instead of at a stock count is the entire job.

**Single-sweeper via the Lease-based `LeaderElectionService` already running in this process**, not a second, independent Postgres-advisory-lock mechanism. `SagaTimeoutSweeper` already pays the cost of Kubernetes Lease-based coordination in Orders.Worker; a second coordination primitive for a second sweeper in the same process would be paying it twice for no benefit the first payment didn't already buy.

**Both new HTTP endpoints are deliberately unauthenticated - named, not defaulted into.** Orders.Worker has no Keycloak client credentials of its own (nothing in this service has ever needed to call outward as an authenticated principal before), and this pass did not extend Milestone 26's JWT wiring to give it one. `/payments/by-order/{orderId}` and `/inventory/backorders` are both new information-disclosure surface as a direct result - a payment's state and amount by order id; every SKU with unfulfilled demand. Smaller exposure than the fully unauthenticated endpoints Milestone 84 closed (no exact stock counts, no full-catalog pricing), but real, and left open specifically because closing it properly means giving Orders.Worker its own service identity - a real, scoped piece of follow-up work, not an oversight to paper over with a comment claiming otherwise.

## Verification performed

Same constraint as every milestone in this pass: no Docker, no live Postgres, Kafka, or a second service to actually call.

- **Full solution build**: 0 warnings, 0 errors, across Orders.Worker, Payments.Service, and Inventory.Service together.
- **`AntiEntropyChecksTests`** (13 facts, `Orders.UnitTests`): every accounted payment state (`Authorized`, `AwaitingPayment`, `Captured`, `Voided`, `Expired`, `Refunded`) is correctly not a divergence; a missing payment and a `Declined` one both are; a backorder on a still-`Backordered` order is not a divergence; one on `Cancelled`, `Confirmed`, `Shipped`, or `Delivered` is.
- **`Orders.ArchitectureTests`** (85/85) and **`Services.ArchitectureTests`** (82/82, up from 80 - the new endpoint and options types were picked up by the existing fitness functions, not exempted from them) both pass unchanged.
- **Not verified in this pass**: `AntiEntropySweeper.SweepAsync` against a real Postgres, a real Payments.Service, and a real Inventory.Service - the actual end-to-end gathering logic (query construction, HTTP deserialization, the leader-election gate) has no test coverage beyond compiling and the pure decision functions it calls. The milestone this most wants to be validated against - running the sweep against a database that still carries Milestone 81's *pre-fix* state and confirming it reports exactly those three divergence classes - could not be attempted at all without a live cluster.

## What was deliberately not done

- **Giving Orders.Worker a service identity to call Payments/Inventory as an authenticated principal.** See Design - named as real follow-up, not silently skipped.
- **A third check for the class of bug Milestone 81's finding 1.2 was** (committed inventory belonging to a cancelled order). This is the one this milestone's own motivating audit would most want covered, and it could not be built with what's currently persisted: `ReservationAllocation` rows - the only record linking a committed reservation back to its order - are deleted the moment `TrySettleReservationAsync` commits them (`WarehouseAllocationStore.TrySettleReservationAsync`'s `RemoveRange(allocations)`), specifically so a settled reservation doesn't linger. Checking this invariant for real needs a permanent ledger of "this order drew down this much stock, from this warehouse, and here is its current status" that survives settlement - a real schema addition to Inventory.Service, not something to bolt onto an existing table without a live database to validate the migration against.
- **Auto-remediation of any divergence found.** See Design for why this is a considered omission, not an unfinished feature.
- **Full-table pagination.** Each tick examines the most recent `BatchSize` (default 200) candidate rows per check, not every row that has ever existed - sufficient to catch a *new* divergence within one sweep interval of it occurring, not to audit a system's entire history in one pass.

## See also

- [Milestone 76: A Capture That Fails Is Now Visible, Not Silent](../domain/milestone-76-settlement-reconciliation.md) — the one reconciliation this system had before this milestone, reactive rather than periodic, and the precedent for "make the divergence visible, let something else decide what to do about it."
- [Milestone 79: Alerting Beyond the One Golden Signal Orders.Api Had](milestone-79-operational-alerts.md) — the DLQ/outbox-backlog/consumer-lag alerting shape `anti_entropy.divergences` is designed to sit alongside.
- [Milestone 81: Cancelling an Order Gives Back Everything It Took](../domain/milestone-81-cancellation-compensation.md) — the audit that found the three specific divergences this milestone builds general machinery to keep catching, and the fix this sweep would have found reason to run in the first place.
