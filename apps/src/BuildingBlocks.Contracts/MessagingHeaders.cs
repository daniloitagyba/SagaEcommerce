namespace BuildingBlocks;

public static class MessagingHeaders
{
    public const string CorrelationId = "correlation-id";

    public const string TraceParent = "traceparent";

    public const string TraceState = "tracestate";

    public const string OriginalTopic = "original-topic";

    public const string OriginalPartition = "original-partition";

    public const string OriginalOffset = "original-offset";

    public const string FailureType = "failure-type";

    public const string AttemptCount = "attempt-count";

    public const string RedriveCount = "redrive-count";
}
