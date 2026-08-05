namespace BuildingBlocks;

public sealed class MessageProcessingOptions
{
    public const string SectionName = "MessageProcessing";

    public int MaximumAttempts { get; init; } = 3;

    public int InitialRetryDelayMilliseconds { get; init; } = 250;

    public int MaximumRetryDelayMilliseconds { get; init; } = 5_000;

    public int InfrastructureRetryDelayMilliseconds { get; init; } = 1_000;
}
