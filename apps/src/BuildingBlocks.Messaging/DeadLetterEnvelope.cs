namespace BuildingBlocks;

// OriginalPayload is always a string on the wire - base64 for topics
// carrying byte[]/Avro payloads, verbatim for topics already carrying JSON
// text - see KafkaDeadLetterPublisherBase's encodePayload parameter. The
// dead-letter envelope stores the original payload uniformly rather than
// assuming any one topic's wire format.
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
