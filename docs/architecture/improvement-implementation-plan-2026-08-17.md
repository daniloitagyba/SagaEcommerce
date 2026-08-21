# Architecture and Engineering Improvement Implementation Plan

## Status and evidence baseline

This is the canonical implementation plan for the service architecture review.
It supersedes the earlier, shorter version of this document while preserving its
completed work. The plan was rebaselined against commit `dde7747` on 2026-08-17.

Evidence collected locally:

- `dotnet build SagaEcommerce.slnx --configuration Release --no-restore` passes
  with zero warnings and zero errors.
- All 415 executed unit tests pass: Orders 278, Storefront 28, Cart 30,
  Catalog 26, Inventory 31, and Payments 22.
- `Services.ArchitectureTests` passes 89/89.
- `Orders.ArchitectureTests` passes 92/93. Its failing assertion requires every
  `Ef*Repository` constructor to receive a resilience pipeline directly, while
  `EfOrderStatusRepository` now delegates to `OrderTransitionExecutor`, which
  owns that pipeline. This is either a stale fitness function or a registration
  defect and is the first release-blocking item below.
- Testcontainers-backed integration tests and live dependency proofs were not
  run locally, in accordance with `ENVIRONMENT.md`; those remain lab gates.

## Implementation progress

The first incremental implementation batch completed after this assessment has:

- corrected the Orders resilience fitness function to follow the transition
  executor responsibility rather than its constructor shape;
- inverted the Orders creation metric behind an application port, removing the
  direct observability dependency from the application project;
- replaced ambient production clocks with injected `TimeProvider` instances in
  durable messaging, retention, Orders, Payments, Inventory, Cart, workers,
  rate limiting, and seed entry points; and adopted the platform
  `FakeTimeProvider` in Orders unit tests;
- enabled development-only first-party OpenAPI endpoints on every HTTP host,
  pinning the patched OpenAPI dependency version;
- introduced a Cart store port, encapsulated Catalog aggregate mutation, and
  expanded architecture tests for both boundaries;
- separated Payments risk history access (EF adapter) from the pure risk
  policy, with persistence-free policy tests.

The remaining physical project splits, contract ownership migration, build-time
OpenAPI compatibility gate, and remote failure proofs remain deliberately
separate delivery batches; they require compatible database and wire migrations
plus lab evidence, not a broad in-place rewrite.

Already completed from the previous plan:

- Inventory rejects non-positive quantities and PostgreSQL guards non-negative
  stock invariants.
- Public checkout accepts item-based requests rather than a client-authored
  authoritative amount.
- The unused order-query gRPC surface was removed through ADR 002.
- API and saga order transitions now share `OrderTransitionExecutor`.

## Executive assessment

The repository already has strong distributed-systems foundations: transactional
outbox/inbox, durable idempotency, saga compensation, CQRS projections, schema
evolution, resilience pipelines, OpenTelemetry, contract tests, property tests,
architecture fitness functions, and Testcontainers. The next improvement is not
to add more patterns. It is to make ownership and dependency direction match the
business boundaries already present in the code.

| Area | Current maturity | Main issue | Recommended direction |
| --- | --- | --- | --- |
| Orders | High | `Orders.Application` depends on integration contracts and static telemetry; lifecycle values still cross layers as strings | Finish the existing physical Clean Architecture split and make Orders own its lifecycle types and decisions |
| Payments | Medium-high | Domain, orchestration, EF, Kafka, endpoints, and risk queries share one project; risk policy depends directly on `PaymentsDbContext` | Create Domain/Application/Infrastructure boundaries and separate pure risk policy from history access |
| Inventory | Medium-high | Rich domain exists, but Kafka processors and HTTP endpoints coordinate EF transactions directly | Move commands and policies into an application layer; keep SQL/EF/Kafka as adapters |
| Cart | Medium | CRDT logic is testable, but endpoints call Redis storage directly and domain records can admit unbounded primitive state | Add use-case and store-port namespaces first; harden quantities, money, and CRDT bounds |
| Catalog | Medium-low | `Product` exposes public setters and endpoints perform mutation and persistence error translation | Encapsulate product changes and introduce small application handlers without creating unnecessary deployables |
| Storefront | Appropriate for a BFF | No domain model is needed, but proxy and checkout workflow code are large and use untyped JSON/client access | Keep it as a BFF; extract typed downstream clients and a checkout coordinator |
| BuildingBlocks | Operationally useful, semantically broad | `BuildingBlocks.Contracts` mixes integration DTOs with order/payment states, cache keys, retry logic, and anti-entropy rules | Split contracts by responsibility and keep shared code technical, stable, and small |
| Orders.Worker | Operationally mature | A large composition root and many hosted capabilities share one process | Modularize registration now; split the deployment only when scaling or isolation evidence justifies it |

## Architectural decisions

### Use DDD selectively

- Orders, Payments, and Inventory are transaction-heavy bounded contexts and
  warrant aggregates, value objects, explicit policies, and application ports.
- Cart warrants a domain model for its CRDT and merge semantics, but not a full
  project-per-layer split yet.
- Catalog should encapsulate product invariants but remain a relatively simple
  document-oriented bounded context.
- Storefront is a BFF and anti-corruption layer. Adding a fake domain layer to it
  would reduce clarity.

### Use Clean Architecture for dependency direction, not folder count

The required direction is:

```text
HTTP/Kafka/Redis/PostgreSQL/MongoDB adapters
                    |
                    v
             Application use cases
                    |
                    v
           Domain model and policies
```

Domain code must not depend on HTTP, Kafka, EF Core, Npgsql, Redis, MongoDB,
OpenTelemetry, or integration-contract assemblies. Application code may define
ports and orchestration, but must not own database records, Kafka envelopes, or
static telemetry implementations.

Physical project splits are recommended now for Payments and Inventory because
their business and coordination code have grown beyond the small-service premise
used in Milestone 61. Cart and Catalog should first enforce the same boundaries
by namespace and architecture tests. A later project split must be justified by
independent ownership, testability, scaling, or deployment needs.

### Keep integration contracts outside domain models

`BuildingBlocks.Contracts` should contain only stable, serialized integration
contracts during the migration. The target is publisher-owned contract groups
for Orders, Payments, and Inventory. Move the following elsewhere:

- order lifecycle and settlement decisions to `Orders.Domain`;
- payment methods and states to `Payments.Domain`;
- `OutboxMessage` to persistence infrastructure;
- retry calculation to messaging/resilience infrastructure;
- cache keys to caching adapters;
- anti-entropy decision logic to the application capability that owns it.

Adapters map domain values to versioned wire values. A wire string remains valid
for compatibility; an unrestricted domain string does not.

### Prefer explicit patterns already proven in the repository

Continue using aggregate roots, value objects, policy/strategy, state-machine
transitions, ports and adapters, transactional outbox/inbox, saga compensation,
CQRS where read/write needs differ, and anti-corruption adapters. Do not add a
generic repository, generic unit of work, mediator pipeline, or mapping framework
merely to make the code look more architectural.

### Apply Clean Code through cohesion and explicit outcomes

- Split a class when it has independent reasons to change, not just because it
  crosses a line-count threshold. Current candidates include the 418-line
  inventory reservation processor, the 358-line Redis cart store, and the large
  Storefront checkout/proxy workflow.
- Keep transport parsing, validation, business decisions, transaction control,
  and response mapping as separate steps with one owner each.
- Prefer domain-specific outcomes such as `NotApplicable`, `PriceMismatch`, or
  `InsufficientStock` over booleans and catch-all exceptions. Exceptions remain
  appropriate for unavailable infrastructure and broken invariants.
- Keep mappings explicit at bounded-context boundaries. Duplication of a small
  mapping is cheaper than coupling domain types to HTTP or Kafka serialization.
- Preserve async I/O, cancellation propagation, source-generated structured
  logging, and narrow interfaces. Do not create an interface for a class that has
  neither an alternate implementation nor a real architectural boundary.
- Use comments and ADRs to explain non-obvious invariants, atomicity, and
  operational trade-offs; do not narrate ordinary code.

## Prioritized implementation plan

### Phase 0: Restore trustworthy gates

Priority: P0. Delivery size: one focused pull request.

1. Reconcile the failing `EfOrderStatusRepository` fitness function with the new
   `OrderTransitionExecutor` ownership model. The rule must verify that every
   database adapter reaches the named resilience pipeline and translates
   infrastructure faults, without requiring an unused constructor dependency.
2. Add an architecture test proving that status-transition policy lives in the
   domain and that API/Worker adapters do not own independent transition tables.
3. Run the full fast-test matrix and formatting gate locally, then the complete
   integration suite in the lab.

Acceptance:

- Release build, all unit tests, and all architecture tests pass.
- The corrected fitness function is demonstrated to fail against a deliberate
  temporary violation.
- No production behavior is changed merely to satisfy a constructor-shape test.

### Phase 1: Finish the Orders boundary inversion

Priority: P1. Delivery size: three to five small pull requests.

1. Introduce domain-owned `OrderStatus`, `OrderTransitionPolicy`, and
   `OrderSettlementDecision`. Keep string conversion in persistence and Kafka
   adapters.
2. Move the transition graph from `BuildingBlocks.Contracts` into Orders.Domain.
   Make the transactional executor consume a domain decision and persist its
   explicit effects: status event, coupon/campaign settlement, customer standing,
   payment settlement command, and compensation command.
3. Remove `OutboxMessage` from `IOrderCreationRepository`. Let application code
   describe an integration event while infrastructure creates and stores the
   persistence record atomically.
4. Remove the `BuildingBlocks.Observability` reference from Orders.Application.
   Record metrics in an outer decorator/adapter or behind a narrow application
   port; continue using structured `ILogger` in handlers.
5. Remove the internal amount-only branch from `CreateOrderCommand`,
   `CreateOrderHandler`, and `Order.Create` after replacing test fixtures with
   line-based builders. The public API migration is complete, but the old model
   remains executable internally.
6. Add focused value objects only where they prevent real invalid states:
   `Sku`, `CurrencyCode`, customer/order identifiers, and payment method. Continue
   using `NodaMoney.Money` rather than introducing another money abstraction.

Acceptance:

- Orders.Domain owns every lifecycle rule and runs without outer-layer packages.
- Orders.Application no longer references integration contracts or telemetry
  implementations.
- API and Worker transition parity and concurrency tests stay green.
- Adding an order status requires one domain change plus explicit adapter mapping,
  not synchronized edits across unrelated projects.

### Phase 2: Establish a real Payments bounded context

Priority: P1. Delivery size: four to six pull requests.

1. Create `Payments.Domain`, `Payments.Application`, and
   `Payments.Infrastructure`; keep `Payments.Service` as the composition root and
   HTTP/Kafka host.
2. Move `Payment`, typed states/methods, authorization expiry, capture, void,
   cancellation, and refund policies into Payments.Domain.
3. Split `PaymentRiskEvaluator` into:
   - a pure `PaymentRiskPolicy` that evaluates supplied history and options;
   - an application port such as `IPaymentHistoryReader`;
   - an EF Core adapter that performs bounded history queries.
4. Convert `PaymentDecisionCoordinator` and settlement processors into application
   handlers whose inputs are transport-neutral commands. Kafka processors should
   deserialize, validate, map, call one handler, and map the outcome.
5. Keep the aggregate and outbox/inbox write in one transaction. Do not hide the
   transaction behind a generic repository.
6. Create a `Payments.IntegrationTests` owner for payment database, inbox/outbox,
   race, and settlement tests currently hosted under Orders integration tests.

Acceptance:

- Risk rules run as pure unit tests with no EF Core or SQLite provider branches.
- Invalid payment state transitions are unrepresentable or rejected by one
  aggregate policy.
- Duplicate, reordered, capture/cancel, and partial-refund races retain their
  current durable behavior in PostgreSQL lab tests.
- The Payments host contains registration and endpoint mapping, not business
  decisions.

### Phase 3: Establish Inventory application and domain ownership

Priority: P1. Delivery size: four to six pull requests.

1. Create `Inventory.Domain`, `Inventory.Application`, and
   `Inventory.Infrastructure`, retaining `Inventory.Service` as the host.
2. Define application commands for reserve, commit, release, restock,
   replenishment, and backorder cancellation. Kafka message types remain adapter
   inputs and never enter domain signatures.
3. Split `InventoryReservationMessageProcessor` by message purpose. Each adapter
   maps and invokes one use case; a transaction coordinator owns inbox, aggregate,
   ledger, and outbox atomicity.
4. Move EF queries out of HTTP endpoints into query handlers/read ports. Preserve
   projection-style SQL where it is clearer and more efficient than materializing
   aggregates.
5. Introduce `Sku`, `WarehouseCode`, `ReservationId`, and positive quantity types
   at the domain boundary, with database constraints as the second line of
   defense.
6. Retain advisory locks, CAS, and specialized allocation SQL. Document them as
   concurrency adapters rather than replacing them with repository abstractions.

Acceptance:

- Inventory.Domain has no EF Core, Npgsql, Kafka, or integration-contract
  dependencies.
- A reserve/commit/release decision is unit-testable without a database.
- Atomicity, per-SKU serialization, backorder release, and restock races pass in
  the lab.
- The current property-based stock invariants also cover multi-warehouse and
  escrow sequences.

### Phase 4: Harden Cart, Catalog, and Storefront proportionately

Priority: P2. Delivery size: two to four pull requests per service.

Cart:

1. Add application use cases and an `ICartStore` port between endpoints and
   `CartStore`; keep Lua/CAS/Redis mechanics in the data adapter.
2. Bound cart quantity, CRDT counters, replica identifiers, metadata size, and
   conversion from `long` counters to API `int` quantities. Add overflow and
   adversarial merge property tests.
3. Use `NodaMoney` or an equivalent validated cart money value for snapshots;
   Orders remains the price authority.

Catalog:

1. Replace public setters on `Product` with `Create` and `UpdateDetails` behavior;
   expose immutable collections and validate `Category` construction.
2. Move category existence checks, uniqueness outcomes, and create/update
   orchestration into application handlers. Mongo duplicate-key translation stays
   in infrastructure.
3. Extend domain encapsulation fitness functions to Catalog; it is currently not
   covered by the no-public-setter rule applied to Orders, Payments, and Inventory.

Storefront:

1. Extract typed Catalog, Cart, Orders, and Inventory clients instead of passing
   untyped JSON objects and string client names through workflows.
2. Move checkout into a coordinator with explicit outcomes for cart unavailable,
   empty cart, price mismatch/reprice, order accepted, and post-order cart-clear
   failure.
3. Keep the BFF datastore-free. Do not add Domain/Application projects.
4. Evaluate YARP direct forwarding for only the one-to-one proxy routes; retain
   custom code for checkout aggregation and the strict header allowlist unless a
   spike proves equivalent security, streaming, and error behavior.

Acceptance:

- Cart and Catalog application behavior can be unit-tested without Redis/MongoDB.
- Catalog entities cannot be temporarily invalid through public setters.
- Storefront downstream contracts are typed and covered by consumer tests.
- The BFF still owns no database or message broker dependency.

### Phase 5: Cross-cutting consistency and contracts

Priority: P2. Delivery size: several independent pull requests.

1. Replace the 24 direct `DateTimeOffset.UtcNow` calls across 15 source files
   wherever time affects durable state, retry, rate limiting, projection, or
   domain behavior. Inject `TimeProvider`; use its delay/timer APIs for testable
   loops. Seeders may receive an explicit timestamp.
2. Replace hand-written fake clocks with the official test time provider and add
   deterministic expiry, retry, hedge, sweeper, and retention tests.
3. Extract composition-root modules for messaging, outbox/inbox, saga,
   projections, anti-entropy, authentication, and health checks. Use typed
   `IValidateOptions<T>` validators; keep `Program.cs` as the readable capability
   map.
4. Generate OpenAPI documents for every HTTP service at build time. Annotate
   response and Problem Details shapes, diff public specifications in CI, and
   keep interactive documentation restricted to an appropriate environment.
5. Standardize boundary validation and RFC 9457 Problem Details mapping while
   preserving domain-specific result types. Avoid one global exception type or a
   generic result abstraction that erases business outcomes.
6. Rename the cross-service `OrdersTelemetry` surface or split it into bounded
   meters/sources. Preserve W3C context propagation and add low-cardinality
   domain metrics for payment, inventory, cart, and catalog.
7. Define compatibility rules for every JSON and Avro contract: owner, key,
   version, required/optional fields, unknown version handling, retention, and
   deprecation window.

Acceptance:

- No ambient clock remains in time-sensitive production behavior.
- Invalid configuration fails at startup with a field-specific message.
- Every public API has a generated, versioned specification and compatibility
  gate.
- Metrics and logs identify the owning bounded context and do not expose personal
  data or credentials.

### Phase 6: Verification and operational proof

Priority: continuous; complete after each affected phase.

1. Add model-based tests for Order, Payment, Inventory, and saga state machines.
2. Expand property tests for money allocation, refunds, stock, CRDT convergence,
   and transition monotonicity.
3. Mutation-test branching domain policies. Replace the repository-wide 18%
   line-coverage floor over time with measured per-critical-assembly line and
   branch baselines; do not set an arbitrary aspirational percentage.
4. Add producer/consumer compatibility tests for checkout and every critical
   Kafka command/reply pair, including duplicate, reordered, unknown-version,
   and poison messages.
5. Run restart, lost-ack, rebalance, clock-skew, dependency-partition, and failed
   compensation proofs in the remote lab.
6. Update the critical-flow matrix, ADRs, dashboards, alerts, and runbooks in the
   same change that introduces a new failure mode.

Acceptance:

- Every critical flow proves happy path, failure, retry, idempotency,
  concurrency, and compensation where applicable.
- Lab evidence demonstrates convergence after process and dependency failures.
- A failed compensation reaches a durable, observable terminal workflow.

## Library decisions

### Adopt in the next relevant phase

| Library or platform API | Use | Decision |
| --- | --- | --- |
| `Microsoft.Extensions.TimeProvider.Testing` | Deterministic time, timers, and delays in tests | Adopt in Phase 5; replace local fake clock classes |
| `Microsoft.AspNetCore.OpenApi` | First-party runtime OpenAPI generation for .NET 10 Minimal APIs | Adopt for HTTP hosts |
| `Microsoft.Extensions.ApiDescription.Server` | Build-time OpenAPI artifacts for compatibility checks | Adopt as a private build asset |
| Built-in options validation source generator | Typed startup validation outside large `Program.cs` lambdas | Adopt without adding another third-party framework |

### Retain and use more consistently

| Existing library | Direction |
| --- | --- |
| `NodaMoney` | Keep as the money/currency implementation; enforce currency consistency at context boundaries |
| `FluentValidation` | Keep for application command validation; do not put it in domain entities |
| `NRules` | Keep behind `IPricingEngine`; domain pricing models must remain independent from the engine |
| `Confluent.Kafka` and Schema Registry | Keep because explicit Kafka semantics are a core learning and operational goal |
| `Microsoft.Extensions.Resilience` and HTTP resilience | Keep; centralize named policy ownership and test retry eligibility |
| OpenTelemetry packages | Keep; split meters/sources by bounded context and preserve semantic conventions |
| `Testcontainers`, `CsCheck`, `PactNet`, and `NetArchTest.Rules` | Keep; improve test ownership and fitness-function semantics rather than replacing them |

### Evaluate through a bounded spike or ADR

| Candidate | Potential value | Adoption condition |
| --- | --- | --- |
| `Vogen` | Source-generated, validated value objects with JSON/EF conversions | Pilot only `Sku` and one identifier in one context; adopt if generated code, migrations, serializers, and architecture tests remain clear |
| `Yarp.ReverseProxy` | Removes hand-written mechanics from Storefront's simple pass-through routes | Adopt only if a spike preserves the explicit header allowlist, auth propagation, streaming, body limits, telemetry, and tests |
| `Respawn` | Faster deterministic database reset for shared Testcontainers fixtures | Evaluate only if lab profiling shows database setup/reset is a material integration-test bottleneck |

### Do not adopt now

| Library/category | Reason |
| --- | --- |
| MediatR or another in-process mediator | Current handlers are explicit and DI registration is manageable; a mediator would add indirection without solving a measured coupling problem |
| AutoMapper or Mapster | Boundary mappings are business-relevant and small enough to remain explicit and reviewable |
| Generic repository/specification frameworks | They would hide EF/Npgsql capabilities and the atomic SQL required for inbox, outbox, CAS, and allocation |
| MassTransit/Wolverine/NServiceBus migration | It would replace proven Kafka/outbox/inbox behavior, alter delivery semantics, and remove part of the lab's purpose. MassTransit v9 also requires an explicit commercial licensing decision |
| Marten or EventStoreDB | The current event store is an audit/rebuild capability, not the write-model source of truth; changing that is a separate architecture decision |
| A second money library | `NodaMoney` already serves this role; two money models would increase conversion and rounding risk |

Primary references used for the library assessment:

- [Testing with `FakeTimeProvider`](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing)
- [ASP.NET Core OpenAPI generation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)
- [YARP direct forwarding](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/direct-forwarding?view=aspnetcore-10.0)
- [Vogen source generator and analyzer](https://github.com/SteveDunn/Vogen)
- [MassTransit Kafka and licensing documentation](https://masstransit.io/documentation/configuration/transports/kafka)
- [Respawn repository](https://github.com/jbogard/Respawn)

## Delivery order and dependencies

1. Phase 0 must merge first because the current architecture test gate is red.
2. Orders boundary inversion precedes contract splitting because it identifies the
   true owner of lifecycle types.
3. Payments and Inventory can then migrate independently, one green pull request
   at a time; neither requires a deployment split.
4. Cart, Catalog, and Storefront improvements are independent after shared
   contract naming is settled.
5. Time, OpenAPI, options validation, and telemetry work should be delivered in
   narrow vertical changes, not a repository-wide rewrite.
6. A messaging framework or worker-deployment split requires a separate ADR and
   measured operational evidence.

## Definition of done for every phase

- Release build with nullable reference types and warnings-as-errors still green.
- Focused unit and architecture tests pass locally.
- A changed external dependency path passes Testcontainers integration and
  contract tests on the remote lab.
- I/O remains asynchronous and cancellation tokens reach the real operation.
- Graceful shutdown, liveness, readiness, and dependency health semantics remain
  meaningful.
- Database and wire changes use expand/contract compatibility and rollback plans.
- New failure modes have structured logs, traces, low-cardinality metrics, alerts,
  and a recovery path without exposing credentials or personal data.
- An ADR records any new framework, deployable, shared abstraction, or semantic
  guarantee that materially changes the system.
