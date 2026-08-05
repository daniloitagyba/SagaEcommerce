namespace BuildingBlocks;

public static class RetryDelayCalculator
{
    public static TimeSpan Calculate(int completedAttempt, int initialDelayMilliseconds, int maximumDelayMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelayMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelayMilliseconds, 1);

        var exponent = Math.Min(completedAttempt - 1, 10);
        var delay = Math.Min(maximumDelayMilliseconds, initialDelayMilliseconds * (1 << exponent));
        return TimeSpan.FromMilliseconds(delay);
    }
}
