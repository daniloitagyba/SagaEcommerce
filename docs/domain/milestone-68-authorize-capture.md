# Milestone 68: Authorize, Then Capture

## What was wrong

A payment was a boolean. `approved: true` meant the money had conceptually moved, at the instant the risk rules said yes. That collapses the distinction every card network actually makes:

- a **hold** placed at checkout, against funds that are still the customer's;
- the **capture** that takes them, when the goods actually ship.

With one boolean there is no way to express "authorized but not yet charged", which in turn means there is nothing to expire, nothing to void when an order is cancelled, and no reason for a payment to have a lifecycle at all. Milestone 66 gave the decision real inputs (customer history, velocity, atypical amounts); this gives the *outcome* somewhere to live.

## The payment method became a business rule

`PaymentMethods` is not a decorative field. It decides whether the two-phase flow applies at all:

| Method | Flow |
| --- | --- |
| `Card` | `Authorized` (hold, with an expiry) → `Captured` \| `Voided` \| `Expired` |
| `Pix` | `Captured` outright — an instant transfer has no hold to place |

Modelling Pix as `Authorized` would create an authorization that no capture command would ever legitimately settle, and that the expiry sweeper would eventually release for a payment that was actually completed. So the method is carried on the order, through the event, and into the payment.

An unrecognised method is **rejected** rather than silently defaulted — charging by a different method than the shopper picked is worse than refusing the order.

## The state machine, and the guard that matters

```
Declined                                  (risk rules said no — terminal)
Captured                                  (Pix)
Authorized ──capture──► Captured          (Card, at confirmation)
           ──void────► Voided             (order cancelled)
           ──expire──► Expired            (nobody ever captured)
```

Both transitions are guarded inside the domain: `TryCapture` and `TrySettleWithoutCapture` only act from `Authorized`, and return `false` otherwise. That is what makes a redelivered capture command a no-op rather than a double charge — the same reasoning as the inbox, applied to a state transition instead of a message id.

The guard also protects the sweeper: a payment captured between the sweeper's claim and its update is left alone, rather than being expired out from under a charge that already happened.

## The expiry sweeper, and why it has no leader election

An authorization is a hold on someone else's money. Placing one and never resolving it — Orders.Worker was down when the order shipped, the capture command was lost, the order sat unfulfilled — leaves funds encumbered indefinitely. Real acquirers expire holds for exactly this reason.

The plan called for reusing `LeaderElectionService`, the Kubernetes Lease that guards Orders.Worker's `SagaTimeoutSweeper`. **That turned out to be the wrong reading of the existing code.** `SagaOrchestrationStore.ClaimTimedOutSql`'s own comment is explicit that leader election there is belt-and-suspenders and `FOR UPDATE SKIP LOCKED` is the actual correctness mechanism.

Since SKIP LOCKED alone makes concurrent sweeps safe — each replica claims a disjoint batch and blocks on nothing — adding a Lease to Payments.Service would have meant new RBAC, a new lease resource, and an in-cluster Kubernetes client in a service that needs none of those, to buy a guarantee the database already provides. So the sweeper uses SKIP LOCKED and no leader election.

## Where capture is triggered, and why it will move

A real storefront captures at **shipment**. This lab's order currently ends its life at `Confirmed`, so that is where `OrderStatusStore` requests capture — a one-line change once Milestone 69 adds the fulfillment states.

Triggering it now rather than leaving orders permanently uncaptured keeps the system correct end to end at every milestone, which matters more than the trigger being in its final place. Requesting it is fire-and-forget: a failed publish is logged, not propagated, because the expiry sweeper is the backstop. A lost capture command degrades to "the customer was not charged", never to "the customer's money is held forever".

`OrderStatusStore`'s CAS now returns `payment_method` alongside `coupon_code`, for the same reason Milestone 67 added the coupon: the statement that decides who actually moved the order is the only safe place to decide who gets to act on it. Knowing the method there also avoids putting a capture command on the topic for every Pix order, to be answered with "already captured".

## Schema evolution, a third time

`OrderCreated` gained `paymentMethod` (v3), with the same backward-compatible mechanism as Milestone 66's `lines`: a schema default, a version constant, and `IsSupported` accepting every version this consumer can read rather than pinning to the newest.

The default matters more than usual here. Reading a missing method as `Card` would leave an authorization on a payment that was charged outright, with no capture command ever arriving to settle it. **`Pix` is the safe reading**, and there is a round-trip test that says so.

One existing test broke on this change — `RoundTripsLineItemsThroughV2` asserted the literal `2` for the schema version. It now asserts the constant, which is what a round-trip test is actually about: "whatever the current writer emits".

## Verification

### Local

158 tests pass (up from 147): 8 new for the authorization state machine, 2 more schema-evolution round trips, and the existing suite updated for the new shape.

### Against the real stack

**Migration backfill.** 87 historical payments became `Pix/Captured` (82) or `Pix/Declined` (5); 78 historical orders became `Pix`. Without the backfill they would sit at `method=''`/`state=''` — a state `Payment.Authorize` can never produce, and one `PaymentStates.IsSettled` cannot reason about, so the sweeper's own guard would have been undefined for every pre-existing row.

**Two-phase vs single-phase**, same SKU, same amount:

| Order | Method | Hold placed? | Final state |
| --- | --- | --- | --- |
| `c-m68-pix` | `Pix` | no | `Captured` |
| `c-m68-card` | `Card` | **yes** (`authorization_expires_at` set) | `Captured` after the capture command |
| `c-m68-bad` | `Bitcoin` | — | `400` — "PaymentMethod must be one of: Card, Pix." |

**The double-settlement guard, end to end.** Publishing a void command directly to `payments.void-requested.v1` for the already-captured card order:

```
"Payment for order a7b79b35... is already Captured - settlement request ignored"
estado apos tentativa de void: Captured (razao: nenhuma)
```

Money that was charged cannot be un-charged by a late or redelivered message.

**The expiry sweeper.** A card authorization seeded as two hours old and never captured:

```
antes:  Authorized
depois: Expired | razao: authorization window elapsed without capture
"Expired 1 card authorization(s) that were never captured"
```

## Stated simplifications

- **Void is wired and unit-tested but not naturally reachable yet.** In today's saga a declined payment and a cancelled order coincide, so the payment is `Declined` (never authorized) and there is no hold to release. It becomes properly reachable in Milestone 69, when an order can be cancelled *after* being confirmed. The consumer path itself is verified above by publishing the command directly.
- **No partial capture.** Real acquirers let you capture less than you authorized (a short shipment). Capture here is all-or-nothing.
- **`Boleto` is not modelled.** It is a third shape again — asynchronous, confirmed hours or days later — and would need its own pending state rather than reusing `Authorized`.
- **The authorization window is 30 minutes**, not the days a real acquirer allows, so the sweeper is observable in a lab session rather than theoretical.
