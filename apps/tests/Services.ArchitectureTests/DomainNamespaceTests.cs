using System.Reflection;
using NetArchTest.Rules;

namespace Services.ArchitectureTests;

// Fitness functions for the four single-project services with a Domain/
// folder: nothing physically stops frameworks leaking into that namespace
// the way project boundaries stop it for Orders, so this checks at the
// namespace level. Catalog.Service's Product failed this for real (MongoDB
// attributes on the entity) until Milestone 61 moved the mapping out.
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
        // Storefront.Service is a BFF/proxy with no Domain namespace of its
        // own (Milestone 45), so the rule here is "never own persistence or
        // messaging at all" - a regression guard against a future project
        // reference reintroducing one of these dependencies transitively.
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
        // BuildingBlocks was split into six projects-per-concern so a
        // dependency-free shared library wouldn't drag EF Core, Kafka,
        // Redis, and OpenTelemetry into every consumer. This guards the
        // split itself: nothing should land in Contracts pulling one back in.
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
