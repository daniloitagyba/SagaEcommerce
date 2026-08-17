namespace BuildingBlocks;

/// <summary>Uniform dead-letter shape, storing the original payload as a string regardless of the topic's wire format.</summary>
public sealed record DeadLetterEnvelope(
    Guid DeadLetterId,
    string OriginalTopic,
    int OriginalPartition,
    long OriginalOffset,
    string? OriginalKey,
    string OriginalPayload,
    string FailureType,
    string FailureMessage,
    int AttemptCount,
    DateTimeOffset FailedAt);
