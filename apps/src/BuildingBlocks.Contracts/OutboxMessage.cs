namespace BuildingBlocks;

public sealed class OutboxMessage
{
    private const int MaximumLastErrorLength = 2_000;

    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? TraceParent { get; private set; }

    public string? TraceState { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(
        Guid eventId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        string correlationId,
        string? traceParent,
        string? traceState)
    {
        return new OutboxMessage
        {
            Id = eventId,
            EventType = eventType,
            Payload = payload,
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            TraceParent = traceParent,
            TraceState = traceState,
            NextAttemptAt = occurredAt
        };
    }

    public void MarkPublished(DateTimeOffset processedAt)
    {
        ProcessedAt = processedAt;
        LastError = null;
    }

    public void MarkFailed(DateTimeOffset failedAt, string error, int maximumRetryDelaySeconds)
    {
        AttemptCount++;
        var exponent = Math.Min(AttemptCount - 1, 6);
        var delaySeconds = Math.Min(maximumRetryDelaySeconds, 1 << exponent);

        NextAttemptAt = failedAt.AddSeconds(delaySeconds);
        LastError = error.Length <= MaximumLastErrorLength
            ? error
            : error[..MaximumLastErrorLength];
    }
}
