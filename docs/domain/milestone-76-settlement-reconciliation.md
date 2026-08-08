# Milestone 76: A Capture That Fails Is Now Visible, Not Silent

## Scope

`payments.settlement-replied.v1` has existed since Milestone 68 - `PaymentSettlementProcessor` publishes it on every capture, void, and refund. Nothing has ever consumed it. Worse: the processor's `if (!changed)` branch, hit whenever `Payment.TryCapture`/`TrySettleWithoutCapture`/`TryRefund` refuses to apply, logged "already settled" and returned without publishing anything at all - conflating two very different situations under one label.

The concrete failure this enables: an order ships, `Orders.Worker` requests a capture, but `PaymentAuthorizationSweeper` has already expired the hold in the window between shipment and the capture command arriving. `TryCapture` returns `false` because the payment is `Expired`, not `Authorized` - not because this is a redelivered command. The old code called this a duplicate, dropped it, and the order went on toward `Delivered` having been shipped and never charged, with nothing anywhere recording that it happened.

## Design

**Distinguishing a true duplicate from a genuine mismatch.** `PaymentSettlementProcessor.ProcessAsync` now checks whether the payment's current state already matches what *this exact operation* would have produced (`Captured` for a capture, `Voided` for a void, `Refunded`-and-fully-refunded for a refund). Only that case is dropped silently as a harmless redelivery. Anything else - most often `Expired` - is a mismatch: a `PaymentSettlementReplied` reply is published carrying the payment's actual state, and a new `SettlementMismatch` log line replaces the old blanket `AlreadySettled` for this branch.

**`OrderSagaReplyConsumer` now subscribes to `payments.settlement-replied.v1`.** This reply doesn't fit the existing 4-step saga state machine at all - by the time an order ships, `SagaOrchestrationState`'s row for it is long gone (removed at `CommitInventory`). `HandleSettlementRepliedAsync` is a standalone reconciliation, not a step: on `State == Expired`, it moves the order to `FulfillmentHold` via the existing compare-and-set, exactly the same mechanism Milestone 69 built for "confirmed, but something needs a human." Any other state (`Captured`, the happy path) is ignored.

**`OrderStatuses` grows `Shipped` as a legal predecessor of `FulfillmentHold`.** It wasn't reachable from there before - the only path in was `Confirmed`/`Picking`. `FulfillmentHold`'s own meaning ("needs a human, don't let it look healthy in a dashboard") already fit this new reason for landing there; this is an expansion of an existing concept, not a new one.

**The same reply topic also carries `PaymentAuthorizationSweeper`'s bulk expiries.** The sweeper has published to this topic since Milestone 68 for holds that timed out with nobody ever requesting a capture - a different root cause, same terminal state (`Expired`), same correct response (a human should look at it). `HandleSettlementRepliedAsync` doesn't need to know which of the two produced the reply; both cases genuinely need `FulfillmentHold`.

## What this doesn't fix

A void that lands on an already-`Expired` payment (an order cancelled after the hold quietly expired) now also publishes a mismatch reply and a `SettlementMismatch` log line, for visibility - but nothing consumer-side reacts differently to it, since the order is already on its way to `Cancelled` regardless and there's no further action to take. Only the capture-mismatch case drives a status change; the general reply-publishing fix covers all three operations uniformly, but the *reconciliation policy* is scoped to the one outcome that loses money silently.

## A real domain gap this surfaced, fixed alongside it

Making `Shipped -> FulfillmentHold -> Cancelled` legal breaks the graph-level guarantee `OrderStatusTransitionTests.MoneyIsOnlyEverSettledOnceAlongAnyPath` was asserting: a path can now legally ask for both a capture (at `Shipped`) and a void (at `Cancelled`) on the same order. That's still safe - `Payment.TryCapture`/`TrySettleWithoutCapture` both guard on `IsAwaitingSettlement`, so once a payment is `Expired` neither a late capture nor a later void can actually move money - but the safety property moved down a layer, from the status graph to the payment aggregate itself. The property test is renamed and its assertion relaxed to what the graph alone actually still guarantees (each action fires at most once, not that they're mutually exclusive), and a new domain-level test (`AnAlreadyExpiredPaymentCannotBeVoidedEither`) makes the layer that now does the real work explicit and checked.

## Live validation (Compose, real cluster)

A real Card order, `Saga:Mode=Both`: `POST /orders` → `Confirmed` (two `Payment` rows, one per saga path, both `Authorized` - an expected `Both`-mode consequence, not new to this milestone). Both rows forced to `Expired` directly (simulating the sweeper winning the race), then advanced through the fulfilment API: `Confirmed -> Picking -> Shipped`. `Shipped` triggers the real capture request.

```
GET /orders/{id} before Shipped: status "Confirmed"
POST .../fulfillment {"status":"Picking"}  -> 200
POST .../fulfillment {"status":"Shipped"}  -> 200
GET /orders/{id} after:            status "FulfillmentHold"
```

Correlated logs, same `correlationId` across both services:

```
Payments.Service:  Settlement mismatch for order {id}: Capture could not apply
                    because payment {paymentId} is already Expired - reply
                    published so the saga can react, not just this log line
Orders.Worker:      Order {id} moved to FulfillmentHold - a settlement reply
                    came back Expired instead of Captured, correlation {id}
```

Before this milestone: the order would have reached `Delivered`, never charged, with neither log line existing.

## Test suite

Full solution, real Testcontainers, on the lab server:

```
Orders.ContractTests         3 passed
Storefront.UnitTests         8 passed
Services.ArchitectureTests  80 passed
Orders.ArchitectureTests    79 passed
Cart.IntegrationTests        4 passed
Catalog.IntegrationTests     7 passed
Orders.IntegrationTests     30 passed  (5 new: PaymentSettlementProcessorTests ×3, OrderSagaReplyConsumerSettlementTests ×2)
Inventory.IntegrationTests  13 passed
Orders.UnitTests           165 passed  (1 new, 1 renamed+relaxed with the reasoning above)
```

389/389, 0 failures.

## What this unblocks

Milestone 77's inventory-timeout compensation follows the same shape this milestone established: a reconciliation reaction to a reply that doesn't fit the existing saga step machinery, landing an order in a state a human can act on rather than letting it complete looking healthy.
