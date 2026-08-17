using System.Reflection;
using NetArchTest.Rules;

namespace Services.ArchitectureTests;

public class NamingConventionTests
{
    private static readonly string[] SmellSuffixes = ["Manager", "Helper", "Util", "Utils"];

    public static IEnumerable<object[]> AssemblyAndSuffixCases()
    {
        Assembly[] assemblies =
        [
            typeof(Cart.Service.Domain.CartLineItem).Assembly,
            typeof(Catalog.Service.Domain.Product).Assembly,
            typeof(Inventory.Service.Domain.InventoryItem).Assembly,
            typeof(Payments.Service.Domain.Payment).Assembly,
            typeof(Storefront.Service.StorefrontEndpoints).Assembly,
            typeof(BuildingBlocks.OrderCreated).Assembly,
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
