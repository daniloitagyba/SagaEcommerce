using System.Reflection;
using Orders.Infrastructure.Persistence;

namespace Orders.ArchitectureTests;

/// <summary>Fitness functions for Orders.Infrastructure/Persistence - the seven EF adapters behind Orders.Application's Ports interfaces.</summary>
public class InfrastructureLayerTests
{
    /// <summary>Every direct EF repository must own its resilience pipeline dependency.</summary>
    [Fact]
    public void DirectEfRepositoriesTakeTheResiliencePipelineProvider()
    {
        var repositories = typeof(EfOrderRepository).Assembly
            .GetTypes()
            .Where(type => type is { Namespace: "Orders.Infrastructure.Persistence", IsClass: true, IsAbstract: false }
                && type.Name.StartsWith("Ef", StringComparison.Ordinal)
                && type.Name.EndsWith("Repository", StringComparison.Ordinal)
                && type != typeof(EfOrderStatusRepository))
            .ToList();

        Assert.NotEmpty(repositories);

        var missing = repositories
            .Where(type => !type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters()
                    .Any(parameter => parameter.ParameterType.Name.StartsWith("ResiliencePipelineProvider", StringComparison.Ordinal))))
            .Select(type => type.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{string.Join(", ", missing)} do(es) not take a ResiliencePipelineProvider<string> - every Ef*Repository must route its database calls through ResilienceExtensions.PostgresPipeline and translate a transient fault into InfrastructureUnavailableException, matching every other adapter in this namespace.");
    }

    [Fact]
    public void OrderStatusRepositoryDelegatesToTheTransitionExecutor()
    {
        var statusRepositoryDependencies = typeof(EfOrderStatusRepository)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.Contains(typeof(OrderTransitionExecutor), statusRepositoryDependencies);

        var executorDependencies = typeof(OrderTransitionExecutor)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        Assert.Contains(executorDependencies, name => name.StartsWith("ResiliencePipelineProvider", StringComparison.Ordinal));
    }
}
