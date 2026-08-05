namespace BuildingBlocks;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; init; } = 5;

    public int PollIntervalMilliseconds { get; init; } = 500;

    public int MaximumRetryDelaySeconds { get; init; } = 60;
}
