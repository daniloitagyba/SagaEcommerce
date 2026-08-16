using System.Reflection;
using Orders.Infrastructure.Persistence;

namespace Orders.ArchitectureTests;

/// <summary>
/// Fitness functions for Orders.Infrastructure/Persistence - the seven EF
/// adapters behind Orders.Application's Ports interfaces.
/// </summary>
public class InfrastructureLayerTests
{
    /// <summary>
    /// Two of the seven adapters (EfOrderSummaryRepository,
    /// EfOrderEventStoreRepository) used to have neither the resilience
    /// pipeline nor InfrastructureUnavailableException translation their
    /// five siblings all carry - a transient Postgres fault propagated raw
    /// past the handler into a generic 500, on exactly the two read
    /// endpoints least likely to be noticed missing it. Checked by
    /// constructor shape (does an Ef*Repository type take a
    /// ResiliencePipelineProvider&lt;string&gt;), not by scanning for the
    /// catch block text, so a repository that reaches the database through
    /// some other means than this project's established pattern still
    /// fails loudly rather than silently.
    /// </summary>
    [Fact]
    public void EveryEfRepositoryTakesTheResiliencePipelineProvider()
    {
        var repositories = typeof(EfOrderRepository).Assembly
            .GetTypes()
            .Where(type => type is { Namespace: "Orders.Infrastructure.Persistence", IsClass: true, IsAbstract: false }
                && type.Name.StartsWith("Ef", StringComparison.Ordinal)
                && type.Name.EndsWith("Repository", StringComparison.Ordinal))
            .ToList();

        // Guards the rule below from passing trivially if every adapter were renamed or moved out of this namespace.
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
}
