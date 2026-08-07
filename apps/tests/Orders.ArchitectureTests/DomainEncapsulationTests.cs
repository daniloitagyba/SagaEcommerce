using System.Reflection;
using NetArchTest.Rules;
using Orders.Domain;

namespace Orders.ArchitectureTests;

// Guardrail: codifies the aggregate/entity pattern this domain already
// follows everywhere - private constructor, static factory, private-set
// properties - so a future member can't quietly bypass invariant
// protection. Records (value objects, drafts) are exempt: their public
// positional constructor and init-only properties are the intended
// DDD value-object shape, not a violation of this rule.
public class DomainEncapsulationTests
{
    private static readonly Type[] EntityTypes = Types.InAssembly(typeof(Order).Assembly)
        .That()
        .ResideInNamespace("Orders.Domain")
        .And()
        .AreClasses()
        .And()
        .ArePublic()
        .GetTypes()
        .Where(type => !IsRecord(type))
        .ToArray();

    public static IEnumerable<object[]> EntityTypeCases() =>
        EntityTypes.Select(type => new object[] { type });

    [Fact]
    public void AtLeastOneEntityTypeIsCoveredByThisGuardrail()
    {
        // Guards the two rules below from passing trivially if every entity were renamed to a record or moved out of the namespace.
        Assert.NotEmpty(EntityTypes);
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
