using Orders.Worker;

namespace Orders.UnitTests;

public sealed class SagaLineCompletionPolicyTests
{
    [Fact]
    public void ReservationsRemainPendingUntilEveryLineReplies()
    {
        var lines = new[] { Line(reserved: true), Line(reserved: null) };

        Assert.Equal(SagaLineCompletion.Pending, SagaLineCompletionPolicy.Reservations(lines));
    }

    [Fact]
    public void CommitAggregationWaitsForEveryReplyEvenAfterOneFailure()
    {
        var lines = new[] { Line(committed: true), Line(committed: false), Line(committed: null) };

        Assert.Equal(SagaLineCompletion.Pending, SagaLineCompletionPolicy.Commits(lines));
    }

    [Fact]
    public void OneExplicitFailureFailsAnAggregateOnceEveryLineReplied()
    {
        var lines = new[] { Line(committed: true), Line(committed: false) };

        Assert.Equal(SagaLineCompletion.Failed, SagaLineCompletionPolicy.Commits(lines));
    }

    [Fact]
    public void EverySuccessfulReplyCompletesTheAggregate()
    {
        var lines = new[] { Line(released: true), Line(released: true) };

        Assert.Equal(SagaLineCompletion.Succeeded, SagaLineCompletionPolicy.Releases(lines));
    }

    private static SagaLineRecord Line(
        bool? reserved = null,
        bool? committed = null,
        bool? released = null) =>
        new(0, Guid.NewGuid(), "SKU", 1, reserved, committed, released);
}
