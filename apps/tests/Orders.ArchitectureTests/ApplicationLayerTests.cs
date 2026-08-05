using System.Reflection;
using NetArchTest.Rules;
using Orders.Application.Ports;
using Orders.Application.UseCases.CreateOrder;
using Orders.Infrastructure.Caching;

namespace Orders.ArchitectureTests;

// Fitness functions for the ports-and-adapters boundary between Orders.Application and its outer layers.
public class ApplicationLayerTests
{
    private static readonly Assembly ApplicationAssembly = typeof(CreateOrderHandler).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(RedisOrderCache).Assembly;

    [Fact]
    public void ApplicationDoesNotDependOnOuterLayers()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Orders.Infrastructure", "Orders.Api", "Orders.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("Confluent.Kafka")]
    [InlineData("StackExchange.Redis")]
    [InlineData("Microsoft.AspNetCore")]
    public void ApplicationDoesNotDependOnAnyInfrastructureFramework(string frameworkNamespace)
    {
        // Orders.Application references BuildingBlocks.Contracts (event
        // records, cache-key builders) and BuildingBlocks.Observability
        // only - split from the former monolithic BuildingBlocks
        // specifically so neither EF Core/Npgsql nor StackExchange.Redis
        // is reachable here at all, not even transitively. This is the
        // real guardrail: nothing stops a use case handler from reaching
        // for IDatabase/DbContext directly instead of going through an
        // Orders.Application.Ports interface, so the fitness function
        // still matters even though it now also holds by construction.
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void PortInterfacesFollowTheIPrefixConvention()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespace("Orders.Application.Ports")
            .And()
            .AreInterfaces()
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData(typeof(IOrderRepository))]
    [InlineData(typeof(IOrderCache))]
    [InlineData(typeof(IIdempotencyStore))]
    [InlineData(typeof(IOrderEventStoreRepository))]
    [InlineData(typeof(IOrderSummaryRepository))]
    public void PortImplementationsLiveInInfrastructure(Type portInterface)
    {
        var implementors = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(portInterface)
            .GetTypes()
            .ToList();

        // A rule with nothing to check passes trivially - guard against a
        // rename/removal silently making this test meaningless.
        Assert.NotEmpty(implementors);

        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(portInterface)
            .Should()
            .ResideInNamespaceStartingWith("Orders.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null ? "no offending types reported" : string.Join(", ", result.FailingTypeNames);
}
