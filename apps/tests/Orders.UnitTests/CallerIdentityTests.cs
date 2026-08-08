using Orders.Application;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 83: the ownership check every ownership-sensitive endpoint and
/// handler shares - MayAccess is the one place this logic is allowed to
/// exist, so every caller of it inherits the same rules automatically.
/// </summary>
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
        // A client-credentials-only caller with neither preferred_username
        // nor sub - only ever expected to reach an Admin-gated route, but
        // defensively refused here too rather than matching on two nulls.
        var caller = new CallerIdentity(CustomerId: null, IsAdmin: false);

        Assert.False(caller.MayAccess(null));
        Assert.False(caller.MayAccess("customer-1"));
    }

    [Fact]
    public void ANullResourceOwnerIsNeverOwnedByANonAdmin()
    {
        // A summary row still mid-projection, or an order the read model
        // hasn't caught up to - absence of proof is not proof of ownership.
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.False(caller.MayAccess(null));
    }

    [Fact]
    public void OwnershipComparisonIsOrdinalNotCaseInsensitive()
    {
        // customerId is a token claim, not a display string - "Customer-1" and "customer-1" are different identities.
        var caller = new CallerIdentity("customer-1", IsAdmin: false);

        Assert.False(caller.MayAccess("Customer-1"));
    }
}
