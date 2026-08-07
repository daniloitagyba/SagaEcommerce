using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace BuildingBlocks;

// Shared shape behind every service's dead-letter publisher: build the
// envelope, copy correlation/trace/redrive-count headers, and produce to
// the dead-letter topic. TValue and encodePayload are the only things that
// differ per topic (byte[]/Avro base64-encodes, JSON-text passes through) -
// everything else stays a thin, service-owned subclass.
public abstract class KafkaDeadLetterPublisherBase<TValue>(
    IProducer<string, string> producer,
    string deadLetterTopic,
    string activityName,
    Func<TValue, string> encodePayload)
{
    private const int MaximumFailureMessageLength = 2_000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    protected async Task PublishCoreAsync(
        ConsumeResult<string, TValue> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var failureMessage = exception.Message.Length <= MaximumFailureMessageLength
            ? exception.Message
            : exception.Message[..MaximumFailureMessageLength];
        var envelope = new DeadLetterEnvelope(
            Guid.NewGuid(),
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            consumeResult.Message.Key,
            encodePayload(consumeResult.Message.Value),
            exception.GetType().Name,
            failureMessage,
            attemptCount,
            DateTimeOffset.UtcNow);

        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);
        using var activity = OrdersTelemetry.StartActivity(
            activityName,
            ActivityKind.Producer,
            traceParent,
            traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", deadLetterTopic);
        activity?.SetTag("messaging.operation.type", "publish");

        var headers = new Headers();
        CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.CorrelationId);
        CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.TraceParent);
        CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.TraceState);
        CopyHeader(consumeResult.Message.Headers, headers, MessagingHeaders.RedriveCount);
        AddHeader(headers, MessagingHeaders.OriginalTopic, consumeResult.Topic);
        AddHeader(headers, MessagingHeaders.OriginalPartition, consumeResult.Partition.Value.ToString(CultureInfo.InvariantCulture));
        AddHeader(headers, MessagingHeaders.OriginalOffset, consumeResult.Offset.Value.ToString(CultureInfo.InvariantCulture));
        AddHeader(headers, MessagingHeaders.FailureType, envelope.FailureType);
        AddHeader(headers, MessagingHeaders.AttemptCount, attemptCount.ToString(CultureInfo.InvariantCulture));

        var message = new Message<string, string>
        {
            Key = consumeResult.Message.Key ?? $"{consumeResult.Topic}-{consumeResult.Partition.Value}-{consumeResult.Offset.Value}",
            Value = JsonSerializer.Serialize(envelope, SerializerOptions),
            Headers = headers
        };

        await producer.ProduceAsync(deadLetterTopic, message, cancellationToken);
    }

    private static void CopyHeader(Headers source, Headers destination, string key)
    {
        var header = source.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        if (header is not null)
        {
            destination.Add(key, header.GetValueBytes());
        }
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    private static void AddHeader(Headers headers, string key, string value)
    {
        headers.Add(key, Encoding.UTF8.GetBytes(value));
    }
}
