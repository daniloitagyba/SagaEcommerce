# Milestone 61: Domain-Boundary Guardrails Across Every Service

## Scope

Milestone 60 asked "does the whole application follow DDD?" and the honest answer was no - only the Orders slice has the full Domain/Application/Infrastructure/Api split, enforced by project references and, since Milestone 60, by fitness-function tests. The other four services (`Cart.Service`, `Catalog.Service`, `Inventory.Service`, `Payments.Service`) each keep a `Domain/` *folder* of encapsulated entities inside a single project, and `Storefront.Service` has no domain model at all (it's a BFF/proxy, appropriately). Given a choice between recreating Milestone 18's full physical split four more times (new projects per service, Dockerfile/compose/kubernetes path changes, real deployment risk for services with 5-8 files each) or enforcing the same boundary at the namespace level without touching how anything builds or deploys, the lighter option was chosen - proportionate to how small these services actually are, and the investigation had already turned up one **real** violation worth fixing regardless of which path was picked.

## Design

**The real violation, fixed first**: `Catalog.Service/Domain/Product.cs` carried `[BsonId]`/`[BsonRepresentation(BsonType.ObjectId)]` directly on the entity - the domain model *was* a MongoDB document mapping, not a persistence-agnostic POCO. Moved to a `BsonClassMap` registered in the new `Catalog.Service/Data/MongoClassMaps.cs`, called from `ProductRepository`'s static constructor so it fires regardless of whether the caller is `Program.cs` or a test fixture. `Product.cs` now has zero `using` statements at all.

**New `Services.ArchitectureTests` project** (`NetArchTest.Rules`, same approach as Milestone 60's `Orders.ArchitectureTests`, but namespace-scoped instead of assembly-scoped since these are single-project services):

- For each of `Cart.Service.Domain`, `Catalog.Service.Domain`, `Inventory.Service.Domain`, `Payments.Service.Domain`: no dependency on `Microsoft.EntityFrameworkCore`, `Npgsql`, `MongoDB.Driver`, `MongoDB.Bson`, `Confluent.Kafka`, `StackExchange.Redis`, or `Microsoft.AspNetCore` - 4 services × 7 frameworks = 28 independent assertions.
- For `Storefront.Service` (no `Domain` namespace to scope to): the whole assembly must not depend on any of those same persistence/messaging frameworks at all - the equivalent rule for a BFF isn't "keep frameworks out of the domain," it's "never own a datastore in the first place." `BuildingBlocks` (its only project reference) already pulls in EF Core Relational and StackExchange.Redis transitively, so this is a real guardrail, not a vacuous one - exactly the same transitive-reference gap Milestone 60 found in `Orders.Application`.

## What didn't work

**`SetRepresentation` doesn't exist in MongoDB.Bson driver 3.10.0.** The first `BsonClassMap` attempt used `classMap.MapIdProperty(p => p.Id).SetRepresentation(BsonType.ObjectId)` - straight from years of MongoDB C# driver v2.x muscle memory. Driver v3 removed that fluent extension; representation is now configured by assigning a specific serializer instance. Found the replacement by loading the actual installed assembly via reflection (`Assembly.Load("MongoDB.Bson").GetTypes()`) rather than guessing from memory a second time - `MongoDB.Bson.Serialization.Serializers.StringSerializer` with a `BsonType` constructor argument.

**The class map silently never ran in the test process.** First fix compiled clean and looked done - `Catalog.IntegrationTests` still failed `InsertedProductCanBeFoundByIdAndListedByCategory` with `Assert.NotNull() Failure: Value is null`. The registration call had been placed in `Catalog.Service/Program.cs`, the application's composition root - which `Catalog.IntegrationTests` never executes, since its test fixtures construct `ProductRepository` directly against a Testcontainers Mongo instance. Moved the `MongoClassMaps.Register()` call into `ProductRepository`'s static constructor instead, so it fires no matter who constructs the repository first.

**Even with the class map registered, the same test kept failing - the driver's implicit `IdGenerator` was gone too.** The old `[BsonId]` attribute combination made the driver auto-register a `StringObjectIdGenerator` behind the scenes, which is what actually assigned a fresh ObjectId back onto `product.Id` on insert when the test left it as `string.Empty`. The manual class map didn't replicate that. Fixed with an explicit `.SetIdGenerator(StringObjectIdGenerator.Instance)` chained onto the same `MapIdProperty` call - verified this was the actual remaining gap, not a new problem, by reading what `InsertedProductCanBeFoundByIdAndListedByCategory` actually asserts rather than guessing again.

**Proved both new fitness-function rules can fail, not just that today's code happens to pass them.** Same discipline as Milestone 60: injected a throwaway `TempViolation.cs` into `Payments.Service/Domain` holding an `NpgsqlConnection` property, reran the suite, watched exactly one of 28 `DomainNamespaceHasNoInfrastructureFrameworkDependency` cases fail naming `Payments.Service.Domain.TempViolation`, then deleted the file and reran clean.

## Results

Full solution, all 9 test projects, verified on the lab server:

```
Services.ArchitectureTests   34/34
Orders.ArchitectureTests     19/19
Orders.UnitTests             32/32
Storefront.UnitTests          2/2
Orders.ContractTests          3/3
Cart.IntegrationTests         4/4
Inventory.IntegrationTests    8/8
Orders.IntegrationTests      23/23
Catalog.IntegrationTests      7/7  (including the fixed Product/ObjectId round-trip)
```

132 tests, 0 failures. `dotnet build SagaEcommerce.slnx` clean, 0 warnings (`TreatWarningsAsErrors=true`).

| Rule | Assertions | Status |
| --- | --- | --- |
| Cart/Catalog/Inventory/Payments `Domain` namespace has no infrastructure-framework dependency | 28 | pass |
| Storefront.Service owns no persistence or messaging at all | 6 | pass |
| Injected violation (`Npgsql` in `Payments.Service.Domain`) is actually caught | - | confirmed, then reverted |

## Running it

```bash
cd apps
dotnet test tests/Services.ArchitectureTests/Services.ArchitectureTests.csproj --logger 'console;verbosity=detailed'
dotnet test tests/Catalog.IntegrationTests/Catalog.IntegrationTests.csproj --filter 'FullyQualifiedName~ProductRepositoryTests'
```
