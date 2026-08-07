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
        // Orders.Application references only BuildingBlocks.Contracts and
        // BuildingBlocks.Observability - nothing stops a use case handler
        // from reaching for IDatabase/DbContext directly instead of an
        // Orders.Application.Ports interface, so this still matters.
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

    // Interface Segregation guardrail: a port growing past this is a port
    // that has stopped being one job for one consumer. 8 leaves real room
    // over today's largest (IOrderCache, 3 members) while still catching a
    // future port creeping into a god-interface no single adapter should
    // have to implement whole.
    private const int MaximumPortMembers = 8;

    [Fact]
    public void PortInterfacesStayFocused()
    {
        var ports = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespace("Orders.Application.Ports")
            .And()
            .AreInterfaces()
            .GetTypes()
            .ToList();

        // Guards the rule below from passing trivially if every port were moved out of the namespace.
        Assert.NotEmpty(ports);

        var oversized = ports
            .Where(port => port.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length > MaximumPortMembers)
            .Select(port => port.Name)
            .ToList();

        Assert.True(
            oversized.Count == 0,
            $"Port(s) exceed the {MaximumPortMembers}-member budget: {string.Join(", ", oversized)}. Split into narrower, consumer-specific interfaces.");
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

        // A rule with nothing to check passes trivially - guard against a rename silently making this test meaningless.
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
