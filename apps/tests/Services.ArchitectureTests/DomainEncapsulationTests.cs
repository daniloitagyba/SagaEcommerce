using System.Reflection;
using NetArchTest.Rules;

namespace Services.ArchitectureTests;

public class DomainEncapsulationTests
{
    public static IEnumerable<object[]> EntityTypeCases()
    {
        Assembly[] assemblies =
        [
            typeof(Payments.Service.Domain.Payment).Assembly,
            typeof(Inventory.Service.Domain.InventoryItem).Assembly,
        ];

        string[] namespaces = ["Payments.Service.Domain", "Inventory.Service.Domain"];

        foreach (var assembly in assemblies)
        {
            foreach (var domainNamespace in namespaces)
            {
                var entityTypes = Types.InAssembly(assembly)
                    .That()
                    .ResideInNamespace(domainNamespace)
                    .And()
                    .AreClasses()
                    .And()
                    .ArePublic()
                    .GetTypes()
                    .Where(type => !IsRecord(type));

                foreach (var entityType in entityTypes)
                {
                    yield return [entityType];
                }
            }
        }
    }

    [Fact]
    public void AtLeastOneEntityTypeIsCoveredByThisGuardrail()
    {
        Assert.NotEmpty(EntityTypeCases());
    }

    [Theory]
    [MemberData(nameof(EntityTypeCases))]
    public void EntitiesAreConstructedOnlyThroughFactoryMethods(Type entityType)
    {
        var publicConstructors = entityType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(
            publicConstructors.Length == 0,
            $"{entityType.Name} exposes a public constructor - build it through a static factory method instead, so invariants can't be bypassed.");
    }

    [Theory]
    [MemberData(nameof(EntityTypeCases))]
    public void EntityPropertiesAreNeverPubliclySettable(Type entityType)
    {
        var publiclySettable = entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetSetMethod(nonPublic: false) is not null)
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            publiclySettable.Count == 0,
            $"{entityType.Name} has publicly settable properties ({string.Join(", ", publiclySettable)}) - state should change only through the entity's own methods.");
    }

    private static bool IsRecord(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(method => method.Name == "<Clone>$");
}
