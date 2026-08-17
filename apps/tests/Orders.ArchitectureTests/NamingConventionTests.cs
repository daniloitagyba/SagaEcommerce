using System.Reflection;
using NetArchTest.Rules;
using Orders.Api.Middleware;
using Orders.Application.UseCases.CreateOrder;
using Orders.Domain;
using Orders.Infrastructure.Caching;
using Orders.Worker;

namespace Orders.ArchitectureTests;

public class NamingConventionTests
{
    private static readonly string[] SmellSuffixes = ["Manager", "Helper", "Util", "Utils"];

    public static IEnumerable<object[]> AssemblyAndSuffixCases()
    {
        Assembly[] assemblies =
        [
            typeof(CorrelationContext).Assembly,
            typeof(CreateOrderHandler).Assembly,
            typeof(Order).Assembly,
            typeof(RedisOrderCache).Assembly,
            typeof(OrderStatusStore).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            foreach (var suffix in SmellSuffixes)
            {
                yield return [assembly, suffix];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AssemblyAndSuffixCases))]
    public void NoClassCarriesADumpingGroundName(Assembly assembly, string suffix)
    {
        var result = Types.InAssembly(assembly)
            .That()
            .AreClasses()
            .Should()
            .NotHaveNameEndingWith(suffix)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null ? "no offending types reported" : string.Join(", ", result.FailingTypeNames);
}
