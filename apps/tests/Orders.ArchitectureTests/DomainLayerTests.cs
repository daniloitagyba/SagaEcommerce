using System.Reflection;
using NetArchTest.Rules;
using Orders.Domain;

namespace Orders.ArchitectureTests;

// Fitness functions: Orders.Domain sits at the center of the onion and must stay dependency-free of every outer layer and framework.
public class DomainLayerTests
{
    private static readonly Assembly DomainAssembly = typeof(Order).Assembly;

    [Fact]
    public void DomainDoesNotDependOnOuterLayers()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Orders.Application", "Orders.Infrastructure", "Orders.Api", "Orders.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("Confluent.Kafka")]
    [InlineData("StackExchange.Redis")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Grpc")]
    public void DomainDoesNotDependOnAnyInfrastructureFramework(string frameworkNamespace)
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOn(frameworkNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void DomainDoesNotDependOnTheRulesEngine()
    {
        // Milestone 66: Orders.Domain owns the pricing model and the
        // IPricingEngine contract; Orders.Application owns rule evaluation.
        // Without this, dropping an NRules Rule next to the model would
        // make the domain unusable without a Rete network compiled behind it.
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("NRules", "FluentValidation")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void ThePricingModelIsReachableFromTheDomain()
    {
        // Guards the rule above from passing trivially if the pricing model were moved out of Orders.Domain entirely.
        var pricingTypes = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespace("Orders.Domain.Pricing")
            .GetTypes()
            .ToList();

        Assert.NotEmpty(pricingTypes);
        Assert.Contains(pricingTypes, type => type.Name == "IPricingEngine");
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null ? "no offending types reported" : string.Join(", ", result.FailingTypeNames);
}
