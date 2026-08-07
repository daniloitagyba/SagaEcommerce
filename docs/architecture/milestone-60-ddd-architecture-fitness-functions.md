# Milestone 60: DDD Architecture Fitness Functions

## Scope

Milestone 18 split `Orders.Api` into `Orders.Domain`/`Orders.Application`/`Orders.Infrastructure`/`Orders.Api`, with the dependency rule enforced by project references - `Orders.Domain` has no package reference to EF Core, so it can't compile against it "even by accident." That's true, but only up to a point: `Orders.Application` references `BuildingBlocks`, and `BuildingBlocks` itself depends on `Microsoft.EntityFrameworkCore.Relational` and `StackExchange.Redis` for its own cross-cutting concerns - so those assemblies are already on `Orders.Application`'s reference graph. Nothing stops a future use-case handler from reaching for `IDatabase` or a `DbContext` directly instead of going through an `Orders.Application.Ports` interface; it would compile cleanly. This milestone turns the layering Milestone 18 established by *convention* into something a CI run actually checks, the same way the other Milestone 59 guardrails turned "we're careful about N+1/races/secrets" into an automated gate instead of a hope.

## Design

New `Orders.ArchitectureTests` project, using `NetArchTest.Rules` (Cecil-based reflection over the *compiled* assemblies, not the source) against the real `Orders.Domain`/`Orders.Application`/`Orders.Infrastructure` DLLs:

- **`DomainLayerTests`** - `Orders.Domain` must not depend on `Orders.Application`/`Orders.Infrastructure`/`Orders.Api`/`Orders.Worker`, and must not depend on `Microsoft.EntityFrameworkCore`, `Npgsql`, `Confluent.Kafka`, `StackExchange.Redis`, `Microsoft.AspNetCore`, or `Grpc` - the actual "no persistence/messaging/web framework in the domain model" rule Milestone 18's design implied but never checked.
- **`ApplicationLayerTests`** - `Orders.Application` must not depend on `Orders.Infrastructure`/`Orders.Api`/`Orders.Worker`, and - the one that actually matters given the `BuildingBlocks` transitive-reference gap above - must not depend on those same four infrastructure frameworks either, even though the assemblies are reachable.
- **`PortInterfacesFollowTheIPrefixConvention`** - every interface in `Orders.Application.Ports` must be named with an `I` prefix. Deliberately scoped to *interfaces only*, not "everything in the namespace": the same namespace also holds plain DTOs and enums (`CachedOrder`, `CacheLookup`, `CacheLookupResult`, `IdempotencyLookup`) that aren't ports at all, just the shapes ports pass around - a blanket "everything here must be an interface" rule would have failed on day one against perfectly correct code.
- **`PortImplementationsLiveInInfrastructure`** - for each of the five port interfaces (`IOrderRepository`, `IOrderCache`, `IIdempotencyStore`, `IOrderEventStoreRepository`, `IOrderSummaryRepository`), its concrete implementation must reside under `Orders.Infrastructure` - adapters stay in the outer layer, never creep into Application or Api. Each theory case first asserts the implementor list isn't empty, so a future rename that silently stops matching anything fails loudly instead of passing by vacuous truth.

## What didn't work

**A blanket `.ImplementInterface(...)` check almost included `IOrderCacheInvalidator` as a false positive.** A first pass grepped for `: IOrderCache` to find `IOrderCache`'s implementor and matched `Orders.Worker.RedisOrderCacheInvalidator : IOrderCacheInvalidator` too - a different, unrelated interface that merely shares the `IOrderCache` prefix. Caught before it became a test, by reading the actual interface declaration rather than trusting the grep - `IOrderCacheInvalidator` lives in `Orders.Worker` by design (it invalidates the cache from the projection side, not through the `Orders.Application.Ports.IOrderCache` contract), so it's correctly outside this milestone's rule entirely.

**Trusting the rule without proof it can fail is the same mistake this whole guardrail series exists to avoid.** Before committing, injected a real violation - a throwaway `TempViolation` class in `Orders.Application` holding a `StackExchange.Redis.IDatabase` property - and reran the suite. `ApplicationDoesNotDependOnAnyInfrastructureFramework(frameworkNamespace: "StackExchange.Redis")` failed immediately, correctly naming `Orders.Application.TempViolation` as the offender. Removed the file, reran, back to 19/19 green - proof the fitness function actually fires, not just that today's code happens to pass it.

## Results

All 19 assertions pass against the current codebase, verified on the lab server against the real compiled assemblies (not just a local guess):

```
Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 0.86s - Orders.ArchitectureTests.dll (net10.0)
```

| Rule | Assertions | Status |
| --- | --- | --- |
| Domain has no outer-layer dependency | 1 | pass |
| Domain has no infrastructure-framework dependency (EF Core, Npgsql, Kafka, Redis, ASP.NET Core, gRPC) | 6 | pass |
| Application has no outer-layer dependency | 1 | pass |
| Application has no infrastructure-framework dependency | 4 | pass |
| Ports follow the `I`-prefix convention | 1 | pass |
| Port implementations live in `Orders.Infrastructure` | 5 | pass |
| Injected violation is actually caught | - | confirmed, then reverted |

Full solution (`dotnet build SagaEcommerce.slnx`) still builds clean with the new project registered in the `.slnx`.

## Running it

```bash
cd apps
dotnet test tests/Orders.ArchitectureTests/Orders.ArchitectureTests.csproj --logger 'console;verbosity=detailed'
```
