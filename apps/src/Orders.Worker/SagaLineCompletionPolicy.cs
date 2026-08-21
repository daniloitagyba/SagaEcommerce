namespace Orders.Worker;

public enum SagaLineCompletion
{
    Pending,
    Succeeded,
    Failed
}

public static class SagaLineCompletionPolicy
{
    public static SagaLineCompletion Reservations(IReadOnlyList<SagaLineRecord> lines) =>
        Evaluate(lines.Select(line => line.Reserved), failFast: true);

    public static SagaLineCompletion Commits(IReadOnlyList<SagaLineRecord> lines) =>
        Evaluate(lines.Select(line => line.Committed), failFast: false);

    public static SagaLineCompletion Releases(IReadOnlyList<SagaLineRecord> lines) =>
        Evaluate(lines.Select(line => line.Released), failFast: false);

    private static SagaLineCompletion Evaluate(IEnumerable<bool?> outcomes, bool failFast)
    {
        var snapshot = outcomes.ToArray();
        if (failFast && snapshot.Any(outcome => outcome == false))
        {
            return SagaLineCompletion.Failed;
        }

        if (snapshot.Length == 0 || snapshot.Any(outcome => outcome is null))
        {
            return SagaLineCompletion.Pending;
        }

        return snapshot.All(outcome => outcome == true)
            ? SagaLineCompletion.Succeeded
            : SagaLineCompletion.Failed;
    }
}
