using BuildingBlocks;

namespace Orders.UnitTests;

public sealed class OutboxMessageTests
{
    [Fact]
    public void CreateInitializesPendingMessageWithCorrelationContext()
    {
        var eventId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

        var message = OutboxMessage.Create(
            eventId,
            "OrderCreated",
            "{\"orderId\":\"test\"}",
            occurredAt,
            "correlation-123",
            "00-trace-parent-01",
            "vendor=value");

        Assert.Equal(eventId, message.Id);
        Assert.Equal(occurredAt, message.NextAttemptAt);
        Assert.Equal("correlation-123", message.CorrelationId);
        Assert.Equal("00-trace-parent-01", message.TraceParent);
        Assert.Equal("vendor=value", message.TraceState);
        Assert.Equal(0, message.AttemptCount);
        Assert.Null(message.ProcessedAt);
    }

    [Fact]
    public void MarkFailedUsesCappedExponentialBackoffAndTruncatesError()
    {
        var message = CreateMessage();
        var failedAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var longError = new string('x', 2_100);

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            message.MarkFailed(failedAt, longError, 10);
        }

        Assert.Equal(8, message.AttemptCount);
        Assert.Equal(failedAt.AddSeconds(10), message.NextAttemptAt);
        Assert.Equal(2_000, message.LastError?.Length);
    }

    [Fact]
    public void MarkPublishedCompletesMessageAndClearsPreviousError()
    {
        var message = CreateMessage();
        var failedAt = DateTimeOffset.UtcNow;
        var processedAt = failedAt.AddSeconds(2);
        message.MarkFailed(failedAt, "Kafka unavailable", 60);

        message.MarkPublished(processedAt);

        Assert.Equal(processedAt, message.ProcessedAt);
        Assert.Null(message.LastError);
        Assert.Equal(1, message.AttemptCount);
    }

    private static OutboxMessage CreateMessage()
    {
        return OutboxMessage.Create(
            Guid.NewGuid(),
            "OrderCreated",
            "{}",
            DateTimeOffset.UtcNow,
            "test-correlation",
            null,
            null);
    }
}
