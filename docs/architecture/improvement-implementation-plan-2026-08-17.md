# Architecture and Engineering Improvement Plan

## Status

Phase 1 started on 2026-08-17. This document remains the implementation plan.
Each remaining phase must be revalidated against the current branch before work starts.

## Goals

- Protect commerce invariants before structural refactoring.
- Establish one owner for order lifecycle rules and their transactional effects.
- Keep domain models independent from transport and persistence contracts.
- Improve cohesion without introducing abstractions or deployables without evidence.
- Preserve the repository's distributed-systems learning goals and existing reliability guarantees.

## Delivery principles

1. Correctness of money, stock, payment, promotion, and order state is release-blocking.
2. Changes to contracts and schemas use expand/contract migrations.
3. Every critical asynchronous path proves idempotency, retry, ordering, and recovery behavior.
4. Refactoring must preserve a green build and focused tests in every pull request.
5. A service or worker is split only when scaling, isolation, ownership, or deployment evidence justifies it.

## Phase 1: Correctness and exposed surface

### Work

1. Reject non-positive quantities in every Inventory aggregate operation.
2. Add PostgreSQL constraints preventing negative available, reserved, and transaction quantities.
3. Add property tests covering random reserve, commit, release, and restock sequences.
4. Move k6, Pact, smoke tests, and documentation from amount-only order creation to item-based checkout.
5. Deprecate and then remove the public amount-only checkout shape.
6. Decide through an ADR whether the unused OrderQuery gRPC endpoint will gain a real client or be removed.

### Progress

- Completed: inventory quantity guards, PostgreSQL check constraints, and randomized inventory invariant coverage.
- Completed: public checkout now accepts items only; load, chaos, contract, integration, and active operational documentation use item-based payloads.
- Completed: ADR 002 removes the unconsumed OrderQuery gRPC endpoint and its HTTP/2 listener and Kubernetes service port.
- Completed: an integration characterization test compares API and saga confirmation effects before transition-store consolidation.

### Acceptance

- No valid command sequence creates negative inventory.
- Invalid messages do not change inventory or enqueue an outbox event.
- Public checkout never accepts a client-authored price as authoritative.
- The gRPC surface has an owner and consumer, or no longer exists.

## Phase 2: Single order lifecycle owner

### Work

1. Move the pure order transition graph and transition decisions into `Orders.Domain`.
2. Introduce one transactional transition executor in `Orders.Infrastructure`.
3. Make both API and Worker paths use the same executor, including ambient Npgsql transactions.
4. Model transition effects explicitly: coupon and campaign settlement, loyalty updates, inventory compensation, payment settlement, and status events.
5. Add parity and concurrency characterization tests before deleting the duplicate transition stores.

### Acceptance

- One implementation changes order status and schedules its durable effects.
- API and saga paths produce equivalent effects for the same transition.
- Compare-and-set, local state, and outbox writes remain in one transaction.
- Adding a lifecycle state does not require synchronized edits in independent stores.

### Progress

- Completed: `OrderTransitionExecutor` owns compare-and-set updates, status events, promotion and coupon settlement, loyalty, payment commands, and API cancellation compensation in one transaction.
- Completed: API and Worker route transitions through the shared executor; the Worker keeps a compatibility adapter for saga-owned ambient transactions.

## Phase 3: DDD and SOLID boundaries

### Work

1. Replace domain strings with focused local types where they prevent invalid states: `OrderStatus`, `PaymentState`, `PaymentMethod`, `Sku`, and `CurrencyCode`.
2. Keep HTTP and Kafka types at the boundary and map them through anti-corruption adapters.
3. Split payment risk evaluation into a pure policy, an application history port, and an EF Core adapter.
4. Treat `BuildingBlocks.Contracts` as integration contracts, not as a shared domain model.
5. Extend architecture tests to prevent domain namespaces from depending on integration contracts.

### Acceptance

- Domain behavior runs without HTTP, Kafka, EF Core, Redis, or contract DTOs.
- Invalid lifecycle values cannot be introduced through unrestricted strings.
- Payment risk rules run as pure unit tests with supplied history.
- New interfaces represent real boundaries or substitutable behavior.

## Phase 4: Cohesion and maintainability

### Work

1. Extract composition-root registration modules for messaging, saga, projections, anti-entropy, and health checks.
2. Split large stores and processors by capability when they own unrelated reasons to change.
3. Replace ambient clocks with `TimeProvider` wherever time affects behavior or durable state.
4. Keep application source free of ordinary comments; record invariants and
   operational constraints in tests, ADRs, runbooks, and milestone reports.
5. Preserve specialized SQL where it provides atomicity; do not introduce a generic repository layer.

### Acceptance

- Composition roots describe capabilities without containing business behavior.
- Business decisions are deterministic under a controlled clock.
- Classes have one cohesive responsibility, independent of an arbitrary line-count threshold.
- Architectural rationale remains discoverable in documentation.

## Phase 5: Reliability and test evidence

### Work

1. Add model-based tests for Order and Payment state machines.
2. Add API-versus-saga transition parity tests.
3. Cover cancellation races with confirmation, capture, return, and compensation.
4. Add a consumer contract for item-based checkout.
5. Exercise duplicate, out-of-order, unknown-version, and poison messages.
6. Run restart, lost-ack, and failed-compensation proofs in the remote lab.

### Acceptance

- Critical consumers prove durable deduplication.
- Out-of-order messages cannot regress state.
- Failed compensation reaches an observable terminal workflow.
- Lab evidence demonstrates convergence after process and dependency failures.

## Phase 6: Production-oriented commerce capabilities

Schedule these only when the project is intended to move beyond a distributed-systems lab:

1. Add a human-facing order number separate from the internal identifier.
2. Persist price source/version in the immutable order-line snapshot.
3. Enforce currency consistency across order, promotion, payment, and refund.
4. Introduce a payment-provider port with external references, signed webhooks, and provider idempotency.
5. Model partial fulfillment per line and shipment if multi-warehouse shipping requires it.
6. Define retention, masking, and audit rules for addresses and other personal data.

## Recommended execution order

The first delivery batch should contain only:

1. Inventory guards and database constraints.
2. Amount-only checkout migration and deprecation.
3. The gRPC ownership ADR.
4. Characterization tests for the two order-transition implementations.

The second batch should consolidate the order lifecycle. It has the highest
architectural return because it removes duplicated rules from the most critical
money, inventory, promotion, and payment path.

## Definition of done

Every phase must include:

- Release build with zero warnings.
- Passing unit and architecture tests locally.
- Passing integration and contract tests in the remote lab when external dependencies are involved.
- Cancellation token propagation and graceful shutdown preservation.
- Structured telemetry for new failure modes without sensitive data.
- Schema and contract compatibility evidence where applicable.
- Updated ADR, runbook, or architecture documentation for decisions with operational consequences.
