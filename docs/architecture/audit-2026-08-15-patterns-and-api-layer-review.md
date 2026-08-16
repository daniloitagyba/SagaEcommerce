# Applied Patterns, Practices and API-Layer Review — Implementation Plan (2026-08-15)

Sixth audit in the current series. The five before it worked outward from
different centres:

| Pass | Centre | Outcome |
|---|---|---|
| [08-14 service and business rules](audit-2026-08-14-service-and-business-rule-review.md) | the saga's seams | 13 findings, closed in `84cd877` |
| [08-15 frontend/catalog/infra](audit-2026-08-15-frontend-catalog-infra-review.md) | the deployment layer | closed |
| [08-15 architecture and cross-cutting](audit-2026-08-15-architecture-and-cross-cutting-review.md) | shared building blocks, config, alerting | 21 findings; phases 1–5 closed in `6118979`, 6–7 open |
| [08-15 build/runtime/time](audit-2026-08-15-build-runtime-and-time-handling-review.md) | the container and the clock | closed in `f839dd5` |
| [08-15 domain and business rules](audit-2026-08-15-domain-and-business-rules-review.md) | the money | closed in `8ce17ec` |

None of them read the **API layer** — the seven HTTP endpoints and the one
gRPC service that are the entire outside surface of `Orders.Api` — or the
**adapter layer** behind it, where the same port is implemented seven times
and only five of those implementations follow the pattern. That is what
this pass covers, plus the test-infrastructure pattern that every
integration test in the repo repeats.

**Method.** I took each pattern the codebase claims to apply — ports and
adapters, ProblemDetails, keyset pagination, rate limiting, one handler
shared across transports — and checked it at *every* site rather than one.
The findings below are all consistency defects: a pattern applied
correctly in most places and silently skipped in a few, which is a harder
class to see than a pattern applied wrongly everywhere.

**Scope note.** The Application layer's port design, the endpoint
authorization model, and the validation approach all came back clean —
see *What is genuinely solid*. I am not padding the list.

---

## Executive summary

| # | Finding | Severity | Theme |
|---|---|---|---|
| 1 | Two of seven repositories skip the resilience pipeline and fault translation entirely | P1 | Adapters |
| 2 | gRPC reports money as `double`, against this codebase's own money standard | P1 | Contracts |
| 3 | Keyset pagination on `order_summaries` has no index that can serve it | P2 | Data |
| 4 | The local rate limiter covers the reads and skips the side-effecting writes | P2 | API |
| 5 | 23 integration test classes each start their own PostgreSQL container | P2 | Testing |

---

## 1. Two of seven repositories skip the resilience pipeline entirely (P1)

`Orders.Infrastructure/Persistence/` holds seven EF adapters. Five follow
one shape exactly:

```csharp
private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);
...
try
{
    return await _pipeline.ExecuteAsync(async ct => /* query */, cancellationToken);
}
catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
{
    throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
}
```

`EfOrderRepository`, `EfOrderStatusRepository`, `EfOrderReturnRepository`,
`EfCouponRepository` and `EfCustomerRepository` all do this.
**`EfOrderSummaryRepository` and `EfOrderEventStoreRepository` do
neither** — no `ResiliencePipelineProvider` in the constructor, no `try`,
no translation. Both are bare `dbContext` queries:

```csharp
public sealed class EfOrderEventStoreRepository(OrdersDbContext dbContext) : IOrderEventStoreRepository
{
    public async Task<IReadOnlyList<OrderEvent>> ListEventsAsync(...)
    {
        var query = dbContext.OrderEvents.AsNoTracking().Where(item => item.OrderId == orderId);
        ...
        return await query.OrderBy(item => item.Id).ToListAsync(cancellationToken);
    }
}
```

Two consequences, both observable:

**a) No retry, no circuit breaker, no timeout.** `ResilienceExtensions.PostgresPipeline`
gives every other read two retries on a transient `NpgsqlException`, a
breaker at a 50% failure ratio, and a 2-second timeout. `GET /orders/summary`
and `GET /orders/{id}/history` get none of it — a transient blip that every
sibling adapter absorbs fails these outright.

**b) The same fault produces a different HTTP status depending on the
endpoint.** With no translation, an `NpgsqlException` propagates raw past
`ListOrderSummariesHandler`/`GetOrderHistoryHandler` to
`UseExceptionHandler`, which emits a generic **500**. Every other endpoint
turns the same underlying fault into **503 with `Retry-After: 5`**. A
client backing off correctly on `/orders/{id}` gets no such signal on
`/orders/summary`, for the identical database outage.

**The 503 handling is also copy-pasted five times.** An identical eight-line
`catch (InfrastructureUnavailableException)` block appears in
`OrderEndpoints` (behind a local `ServiceUnavailable` helper),
`CancellationEndpoints:39`, `FulfillmentEndpoints:78` and
`ReturnEndpoints:80` — same `Retry-After`, same title, same detail, four
independent copies. `OrderQueryGrpcService` has no equivalent at all, so a
Postgres fault there surfaces as an `Unknown` RPC status rather than
`Unavailable`.

**Fix.** The idiomatic mechanism is already in this codebase:
`BadHttpRequestExceptionHandler` is a registered `IExceptionHandler` doing
exactly this job for a different exception.

1. Add `InfrastructureUnavailableExceptionHandler : IExceptionHandler`
   next to it — sets 503, sets `Retry-After`, writes ProblemDetails — and
   register it in `Program.cs`. Delete the four per-endpoint `catch`
   blocks and the `ServiceUnavailable` helper.
2. Give `EfOrderSummaryRepository` and `EfOrderEventStoreRepository` the
   same constructor-injected pipeline and translating `catch` as their
   five siblings.
3. Map `InfrastructureUnavailableException` to
   `StatusCode.Unavailable` in `OrderQueryGrpcService` (a gRPC
   `Interceptor` is the equivalent of the `IExceptionHandler` above if
   more than one RPC ever needs it; one `catch` is fine for one method).

An architecture fitness function in `Orders.ArchitectureTests` asserting
every `Ef*Repository` type takes a `ResiliencePipelineProvider<string>`
constructor parameter would stop the eighth adapter from repeating this —
the same guardrail style the repo already uses for domain purity.

---

## 2. gRPC reports money as `double` (P1)

`Orders.Api/Protos/order_query.proto`:

```protobuf
message GetOrderResponse {
  string id = 1;
  string customer_id = 2;
  double amount = 3;      // <-- money in binary floating point
  ...
}
```

and `OrderQueryGrpcService.GetOrder`:

```csharp
Amount = (double)order.Amount,
```

This contradicts the standard the rest of the codebase holds itself to,
emphatically and everywhere else:

- `Orders.Domain` takes a dependency on **NodaMoney** specifically so that
  "percentage discounts and the per-line discount allocation round
  correctly by construction instead of by review" (its csproj comment).
- `MoneyAllocation` allocates over **integer minor units** with cumulative
  floor division, written because NodaMoney's own `Split` could return a
  negative share about 1 in 1,000 times.
- `orders.amount_cents` is stored as a `bigint`; both status stores read it
  back as `reader.GetInt64(4) / 100m`.

A `decimal` amount like `1234.56` has no exact `double` representation, so
the gRPC transport can report a different value from the REST endpoint for
the same order — for a read whose whole stated purpose is being "the same
read, same handler, same cache, same database, no duplicated business
logic" (the service's own doc comment). The one thing it does duplicate is
the money projection, and that copy is lossy.

**Fix.** Use one of the two standard gRPC money representations:

- `int64 amount_cents = 3;` — matches how the column is already stored, so
  the service returns the value with no conversion at all; or
- `string amount = 3;` — exact decimal text, if the client would rather
  parse than scale.

Prefer the first: `Order.Amount`'s `decimal` is itself derived from
`amount_cents`, so this removes a conversion instead of adding one.
Renaming the field is a breaking proto change in principle — in practice
this service has **no client anywhere in the repository** (the previous
audit's finding 16, still open), so there is nothing to break, and the
change should happen before that stops being true.

---

## 3. Keyset pagination on `order_summaries` has no index that can serve it (P2)

`EfOrderSummaryRepository.ListAsync` implements cursor pagination
carefully and deliberately — keyset, not `OFFSET`, with a comment
explaining why the tie-break avoids `Guid.CompareTo` (not every provider
translates it). The query filters on `status` and `customer_id`, seeks on
`projected_at`, and orders by:

```csharp
.OrderByDescending(item => item.ProjectedAt)
.ThenByDescending(item => item.OrderId)
.Take(limit)
```

The table's only index (`OrdersDbContext.cs:299`) is:

```csharp
summary.HasIndex(item => new { item.Status, item.OrderCreatedAt })
    .HasDatabaseName("ix_order_summaries_status");
```

Three mismatches:

- The sort is on `projected_at`; the index's second column is
  `order_created_at`. **Nothing in the codebase orders by
  `order_created_at`** — the index serves a query shape that is never
  issued.
- The cursor seeks on `projected_at`, which no index covers, so Postgres
  sorts the whole filtered set on every page.
- `customer_id` — the filter a shopper's own listing *always* applies — is
  not indexed at all.

Keyset pagination exists to make deep pages cheap. Implemented against no
supporting index it returns correct, stable results with none of that
benefit: page 50 costs the same full sort as page 1. The pattern is right
and its entire payoff is unrealised.

**Fix.** One migration:

```sql
CREATE INDEX ix_order_summaries_keyset ON order_summaries (projected_at DESC, order_id DESC);
CREATE INDEX ix_order_summaries_customer_keyset ON order_summaries (customer_id, projected_at DESC, order_id DESC);
```

and drop `ix_order_summaries_status` unless a status-filtered listing is
common enough to want `(status, projected_at DESC, order_id DESC)`
instead — which is the shape that would actually serve
`?status=Confirmed`. Confirm with `EXPLAIN (ANALYZE)` on a seeded table
before and after; the repo's `scripts/live-proofs/` convention is the right
home for that measurement.

---

## 4. The local rate limiter covers the reads and skips the side-effecting writes (P2)

`RequireRateLimiting(RateLimitingExtensions.OrdersPolicy)` is applied per
endpoint. Coverage today:

| Endpoint | Local limiter |
|---|---|
| `POST /orders` | ✅ (via `MapGroup`) |
| `GET /orders/{id}` | ✅ (via `MapGroup`) |
| `GET /orders/summary` | ✅ explicit |
| `GET /orders/{id}/history` | ✅ explicit |
| `POST /orders/{id}/cancellation` | ❌ |
| `POST /orders/{id}/returns` | ❌ |
| `POST /orders/{id}/fulfillment` | ❌ |

Every read is covered; every write except order creation is not. That is
backwards from the cost profile — a return queues a refund command **and**
one restock command per SKU; a cancellation queues a payment cancellation,
inventory compensation and a saga flag. Those are the expensive,
side-effect-producing operations.

**Stated precisely, because it matters:** these three endpoints are not
unprotected. `DistributedRateLimitingMiddleware` is mounted globally in
`Program.cs:150`, after authentication, and applies to every request
including these. So this is **defence in depth applied unevenly**, not an
open door — the Redis-backed per-caller limiter still covers them, and
only the local in-process token bucket is missing. It is worth fixing
because the omission is silent, was almost certainly not a decision, and
the distributed limiter is the one that *fails open* when Redis is
unavailable (`RedisSlidingWindowRateLimiter` catches infrastructure faults
and allows the request — correctly, and with a metric, but that is exactly
when the local bucket would be the remaining guard).

**Fix.** Move all seven endpoints under one `MapGroup("/orders")` that
carries `RequireRateLimiting` once, the way `OrderEndpoints` already does
for its two — the per-endpoint repetition is what let three of them drift.
Add a test asserting every route in the `Orders`/`Returns`/`Fulfillment`
tag set carries the rate-limiting metadata; endpoint metadata is
inspectable from `EndpointDataSource` in a `WebApplicationFactory` test,
and `OrdersApiFactory` already exists.

---

## 5. Twenty-three integration test classes each start their own PostgreSQL container (P2)

Every integration test class in the repo constructs its own container:

```csharp
private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .WithDatabase("orders_test")
    .WithUsername("test_user")
    .WithPassword("test-password-not-a-secret")
    .Build();
```

23 occurrences across `Orders.IntegrationTests` (17) and
`Inventory.IntegrationTests` (6), each followed by `StartAsync()` and
`Database.MigrateAsync()` in `InitializeAsync`. xUnit runs test *classes*
in parallel by default, so this is 23 PostgreSQL containers and 23 full
migration runs per CI pass — and `Orders.Infrastructure` alone has 25
migrations to replay each time.

The one shared fixture that exists (`OrdersApiFactory`, used via
`IClassFixture` by `OrdersApiHttpTests`) shows the pattern is understood;
it just was not extended to the rest.

This is the single largest lever on CI wall-clock time in the repo, and it
compounds: every new integration test adds another container.

**Fix.** An xUnit `ICollectionFixture` holding one container per test
project, with per-test isolation by schema or by unique table prefix
rather than by container:

```csharp
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

Migrate once in the fixture; give each test class its own schema
(`SET search_path`) so parallel classes stay isolated. Tests that
deliberately need a pristine database (`PaymentPrimaryMigrationTests`,
which exercises a migration itself) opt out and keep their own container —
that is a legitimate exception, not a reason to skip the change.

Measure before and after: `dotnet test` wall-clock for the two integration
projects is the number that has to move for this to have been worth doing.

---

## Implementation plan

### Phase 1 — Make infrastructure faults uniform (1 session)

Finding 1. Highest value: it is a live behavioural inconsistency on two
endpoints, and it removes four copies of the same catch block.

1. `InfrastructureUnavailableExceptionHandler : IExceptionHandler`,
   registered before `BadHttpRequestExceptionHandler`.
2. Delete the four per-endpoint catches and the `ServiceUnavailable`
   helper.
3. Give `EfOrderSummaryRepository` and `EfOrderEventStoreRepository` the
   pipeline and the translating catch.
4. Map the exception to `StatusCode.Unavailable` in the gRPC service.
5. Fitness function: every `Ef*Repository` takes a
   `ResiliencePipelineProvider<string>`.

**Done when:** stopping Postgres and hitting `GET /orders/summary` returns
503 with `Retry-After`, identical to `GET /orders/{id}`.

### Phase 2 — Stop shipping money as a double (1 session)

Finding 2. `int64 amount_cents` in the proto, no cast in the service, and a
test asserting the gRPC and REST reads agree exactly on an amount chosen to
be unrepresentable in binary64 (e.g. `1234.56`).

**Done when:** no `(double)` cast remains anywhere money is projected.

### Phase 3 — Index the pagination, close the limiter gap (1 session)

Findings 3 and 4, both small and independent.

1. Migration adding the two keyset indexes; drop or reshape
   `ix_order_summaries_status`. `EXPLAIN (ANALYZE)` before/after, recorded.
2. Single `MapGroup` carrying `RequireRateLimiting` for all seven
   endpoints, plus the endpoint-metadata test.

**Done when:** the summary query plan shows an index scan rather than a
sort, and a test fails if a new endpoint is added without rate limiting.

### Phase 4 — Shared test containers (1 session)

Finding 5. Structural, and the one with a number attached: record
`dotnet test` wall-clock for `Orders.IntegrationTests` and
`Inventory.IntegrationTests` before starting, and again after, in the
milestone note.

---

## What is genuinely solid

Checked deliberately, held up, and worth recording so a later pass does not
re-audit it:

- **The port design is genuinely hexagonal, not nominally so.**
  `IOrderRepository` (read) and `IOrderCreationRepository : IOrderRepository`
  (write) are split with a stated reason — "so read/status use cases do not
  depend on idempotency or outbox persistence concerns they never use".
  `CouponReservation`'s doc comment explains why the claim *cannot* be a
  separate call. These are interfaces designed from the use case inward,
  which is the part of the pattern most codebases skip.
- **The authorization model is consistent and correctly shaped.** All seven
  endpoints carry an explicit policy; `POST /orders` overwrites the body's
  `customerId` from the token for non-admins rather than validating it,
  "so there is no window where a crafted body value is ever used even
  transiently"; and both the REST and gRPC reads return 404 rather than 403
  for someone else's order, so probing an id cannot distinguish "not yours"
  from "doesn't exist".
- **ProblemDetails usage distinguishes 409 from 400 correctly and for
  articulated reasons** — a coupon losing its last slot mid-request, a
  catalog price moving under an `ExpectedSubtotal`, and an idempotency-key
  reuse are all "well-formed but the world changed", and all three say so
  in a comment rather than just picking a status.
- **One handler really is shared across both transports.** `GetOrderHandler`
  backs `GET /orders/{id}` and the gRPC `GetOrder` with no duplicated
  business logic — finding 2 above is about the projection of one field,
  not about the pattern, which holds.
- **Validation is one approach, not three.** FluentValidation appears
  exactly once, for `CreateOrderCommand`, with a comment explaining it was
  adopted when that request "grew a conditional shape". No competing
  validation framework, no scattered manual checks in the endpoints.
