using Orders.Application;

namespace Orders.UnitTests;

/// <summary>The ownership check every ownership-sensitive endpoint and handler shares via CallerIdentity.MayAccess.</summary>
public class CallerIdentityTests
{
    [Fact]
    public void AnOwnerMayAccessTheirOwnResource()
    {
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.True(caller.MayAccess("customer-1"));
    }

    [Fact]
    public void ANonOwnerMayNotAccessSomeoneElsesResource()
    {
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.False(caller.MayAccess("customer-2"));
    }

    [Fact]
    public void AnAdminMayAccessAnyResourceRegardlessOfWhoTheyAre()
    {
        var caller = new CallerIdentity(CustomerId: null, IsAdmin: true);

        Assert.True(caller.MayAccess("customer-1"));
        Assert.True(caller.MayAccess(null));
    }

    [Fact]
    public void ANonAdminWithNoIdentityMayNotAccessAnythingEvenAnUnownedResource()
    {
        var caller = new CallerIdentity(CustomerId: null, IsAdmin: false);

        Assert.False(caller.MayAccess(null));
        Assert.False(caller.MayAccess("customer-1"));
    }

    [Fact]
    public void ANullResourceOwnerIsNeverOwnedByANonAdmin()
    {
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.False(caller.MayAccess(null));
    }

    [Fact]
    public void OwnershipComparisonIsOrdinalNotCaseInsensitive()
    {
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.False(caller.MayAccess("Customer-1"));
    }
}
