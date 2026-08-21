using System.Reflection;
using NetArchTest.Rules;

namespace Services.ArchitectureTests;

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
        var assembly = typeof(Storefront.Service.StorefrontEndpoints).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void CartEndpointsDependOnTheCartStorePortInsteadOfTheRedisAdapter()
    {
        var assembly = typeof(Cart.Service.Endpoints.CartEndpoints).Assembly;

        var result = Types.InAssembly(assembly)
            .That()
            .ResideInNamespace("Cart.Service.Endpoints")
            .ShouldNot()
            .HaveDependencyOn("Cart.Service.Data.CartStore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void PaymentRiskPolicyHasNoEfCoreDependency()
    {
        var assembly = typeof(Payments.Service.Risk.PaymentRiskPolicy).Assembly;

        var result = Types.InAssembly(assembly)
            .That()
            .HaveName("PaymentRiskPolicy")
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
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
    [InlineData("System.Net.Http")]
    [InlineData("OpenTelemetry")]
    [InlineData("Polly")]
    public void BuildingBlocksContractsHasNoFrameworkDependency(string frameworkNamespace)
    {
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
