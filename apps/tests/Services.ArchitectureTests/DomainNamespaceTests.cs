using System.Reflection;
using NetArchTest.Rules;

namespace Services.ArchitectureTests;

// Fitness functions for the four single-project services that still keep a
// Domain/ folder of encapsulated entities: nothing physically stops
// persistence/messaging/web frameworks from leaking into that namespace the
// way project boundaries stop it for Orders (Milestone 60) - this checks it
// at the namespace level instead. Catalog.Service's Product used to fail
// this for real (MongoDB.Bson attributes on the entity itself) until
// Milestone 61 moved that mapping into Catalog.Service.Data.
public class DomainNamespaceTests
{
    private static readonly string[] InfrastructureFrameworkNamespaces =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MongoDB.Driver",
        "MongoDB.Bson",
        "Confluent.Kafka",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
    ];

    public static IEnumerable<object[]> DomainNamespaceAgainstEachFramework()
    {
        (Assembly Assembly, string Namespace)[] domains =
        [
            (typeof(Cart.Service.Domain.CartLineItem).Assembly, "Cart.Service.Domain"),
            (typeof(Catalog.Service.Domain.Product).Assembly, "Catalog.Service.Domain"),
            (typeof(Inventory.Service.Domain.InventoryItem).Assembly, "Inventory.Service.Domain"),
            (typeof(Payments.Service.Domain.Payment).Assembly, "Payments.Service.Domain"),
        ];

        foreach (var domain in domains)
        {
            foreach (var frameworkNamespace in InfrastructureFrameworkNamespaces)
            {
                yield return [domain.Assembly, domain.Namespace, frameworkNamespace];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DomainNamespaceAgainstEachFramework))]
    public void DomainNamespaceHasNoInfrastructureFrameworkDependency(
        Assembly assembly, string domainNamespace, string frameworkNamespace)
    {
        var result = Types.InAssembly(assembly)
            .That()
            .ResideInNamespace(domainNamespace)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("MongoDB.Driver")]
    [InlineData("MongoDB.Bson")]
    [InlineData("Confluent.Kafka")]
    [InlineData("StackExchange.Redis")]
    public void StorefrontServiceOwnsNoPersistenceOrMessaging(string frameworkNamespace)
    {
        // Storefront.Service is deliberately a BFF/proxy with no Domain
        // namespace of its own (Milestone 45) - the equivalent rule for it
        // isn't "keep persistence out of Domain", it's "never own
        // persistence or messaging at all". Its only project reference is
        // BuildingBlocks.Observability (split from the former monolithic
        // BuildingBlocks in the projects-per-concern refactor), which
        // itself has no EF Core/Npgsql/MongoDB/Kafka/Redis dependency -
        // this test now holds true by construction, and exists as a
        // regression guard against a future project reference
        // reintroducing one of those dependencies transitively.
        var assembly = typeof(Storefront.Service.KeycloakTokenProvider).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("MongoDB.Driver")]
    [InlineData("MongoDB.Bson")]
    [InlineData("Confluent.Kafka")]
    [InlineData("StackExchange.Redis")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("OpenTelemetry")]
    [InlineData("Polly")]
    public void BuildingBlocksContractsHasNoFrameworkDependency(string frameworkNamespace)
    {
        // BuildingBlocks was split into six projects-per-concern
        // (Contracts/Messaging/Persistence/Caching/Observability/
        // Resilience) precisely so a dependency-free shared library
        // (event/command records, cache-key builders, retry math) wouldn't
        // keep dragging EF Core, Kafka, Redis, and OpenTelemetry into every
        // consumer - including Orders.Domain and Orders.Application, which
        // have their own fitness functions banning exactly those
        // frameworks. This guards the split itself: nothing should ever
        // land in Contracts that pulls one of these back in.
        var assembly = typeof(BuildingBlocks.OrderCreated).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null ? "no offending types reported" : string.Join(", ", result.FailingTypeNames);
}
