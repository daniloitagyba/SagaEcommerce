using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payments.Service;

namespace Payments.UnitTests;

/// <summary>
/// KafkaDeadLetterPublisherBase&lt;TValue&gt; - shared by every dead-letter
/// publisher in the codebase (Payments/Orders.Worker/Inventory alike) -
/// had no coverage anywhere despite being the one place that decides what
/// actually reaches a dead-letter topic when processing fails. Tested via
/// KafkaDeadLetterPublisher, Payments.Service's own concrete subclass
/// (byte[]-valued, base64-encoded - exercises the encodePayload path
/// too, not just the string-passthrough one PaymentDecisionDeadLetterPublisher
/// would), against a fake IProducer&lt;string, string&gt; that just records
/// what it was asked to produce - no broker involved, the envelope-building
/// logic has no dependency on one.
/// </summary>
public sealed class KafkaDeadLetterPublisherBaseTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheEnvelopeCarriesTheOriginalTopicPartitionOffsetAndFailureDetails()
    {
        var producer = new RecordingProducer();
        var publisher = new KafkaDeadLetterPublisher(producer, Options.Create(new PaymentsKafkaOptions { DeadLetterTopic = "payments.decisions.dlq.v1" }));
        var payloadBytes = Encoding.UTF8.GetBytes("{\"bad\":true}");
        var consumeResult = BuildConsumeResult(topic: "orders.created.v1", partition: 2, offset: 42, key: "order-1", value: payloadBytes);

        await publisher.PublishAsync(consumeResult, new InvalidOperationException("boom"), attemptCount: 3, CancellationToken.None);

        Assert.NotNull(producer.LastMessage);
        Assert.Equal("payments.decisions.dlq.v1", producer.LastTopic);

        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(producer.LastMessage!.Value, SerializerOptions)!;
        Assert.Equal("orders.created.v1", envelope.OriginalTopic);
        Assert.Equal(2, envelope.OriginalPartition);
        Assert.Equal(42, envelope.OriginalOffset);
        Assert.Equal("order-1", envelope.OriginalKey);
        Assert.Equal(Convert.ToBase64String(payloadBytes), envelope.OriginalPayload);
        Assert.Equal(nameof(InvalidOperationException), envelope.FailureType);
        Assert.Equal("boom", envelope.FailureMessage);
        Assert.Equal(3, envelope.AttemptCount);
    }

    [Fact]
    public async Task CorrelationAndTraceHeadersAreCopiedOntoTheDeadLetterMessage()
    {
        var producer = new RecordingProducer();
        var publisher = new KafkaDeadLetterPublisher(producer, Options.Create(new PaymentsKafkaOptions { DeadLetterTopic = "payments.decisions.dlq.v1" }));
        var consumeResult = BuildConsumeResult(topic: "orders.created.v1", partition: 0, offset: 1, key: "order-2", value: []);
        consumeResult.Message.Headers.Add(MessagingHeaders.CorrelationId, Encoding.UTF8.GetBytes("correlation-xyz"));
        consumeResult.Message.Headers.Add(MessagingHeaders.TraceParent, Encoding.UTF8.GetBytes("00-trace-01"));

        await publisher.PublishAsync(consumeResult, new InvalidOperationException("failed"), attemptCount: 1, CancellationToken.None);

        var headers = producer.LastMessage!.Headers;
        Assert.Equal("correlation-xyz", HeaderValue(headers, MessagingHeaders.CorrelationId));
        Assert.Equal("00-trace-01", HeaderValue(headers, MessagingHeaders.TraceParent));
        Assert.Equal("orders.created.v1", HeaderValue(headers, MessagingHeaders.OriginalTopic));
        Assert.Equal("1", HeaderValue(headers, MessagingHeaders.AttemptCount));
    }

    [Fact]
    public async Task AnExceptionMessageOverTwoThousandCharactersIsTruncatedNotDropped()
    {
        var producer = new RecordingProducer();
        var publisher = new KafkaDeadLetterPublisher(producer, Options.Create(new PaymentsKafkaOptions { DeadLetterTopic = "payments.decisions.dlq.v1" }));
        var longMessage = new string('x', 5_000);
        var consumeResult = BuildConsumeResult(topic: "orders.created.v1", partition: 0, offset: 1, key: "order-3", value: []);

        await publisher.PublishAsync(consumeResult, new InvalidOperationException(longMessage), attemptCount: 1, CancellationToken.None);

        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(producer.LastMessage!.Value, SerializerOptions)!;
        Assert.Equal(2_000, envelope.FailureMessage.Length);
    }

    /// <summary>Falls back to a topic-partition-offset composite key when the original message had none - a null Kafka key is legal, but the dead-letter topic still needs one to route by.</summary>
    [Fact]
    public async Task AMissingOriginalKeyFallsBackToATopicPartitionOffsetComposite()
    {
        var producer = new RecordingProducer();
        var publisher = new KafkaDeadLetterPublisher(producer, Options.Create(new PaymentsKafkaOptions { DeadLetterTopic = "payments.decisions.dlq.v1" }));
        var consumeResult = BuildConsumeResult(topic: "orders.created.v1", partition: 3, offset: 7, key: null!, value: []);

        await publisher.PublishAsync(consumeResult, new InvalidOperationException("failed"), attemptCount: 1, CancellationToken.None);

        Assert.Equal("orders.created.v1-3-7", producer.LastMessage!.Key);
    }

    private static ConsumeResult<string, byte[]> BuildConsumeResult(string topic, int partition, long offset, string key, byte[] value) =>
        new()
        {
            Topic = topic,
            Partition = new Partition(partition),
            Offset = new Offset(offset),
            Message = new Message<string, byte[]> { Key = key, Value = value, Headers = [] }
        };

    private static string? HeaderValue(Headers headers, string key) =>
        headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    private sealed class RecordingProducer : IProducer<string, string>
    {
        public string? LastTopic { get; private set; }
        public Message<string, string>? LastMessage { get; private set; }

        public Task<DeliveryResult<string, string>> ProduceAsync(string topic, Message<string, string> message, CancellationToken cancellationToken = default)
        {
            LastTopic = topic;
            LastMessage = message;
            return Task.FromResult(new DeliveryResult<string, string>
            {
                Topic = topic,
                Partition = new Partition(0),
                Offset = new Offset(0),
                Message = message
            });
        }

        public Task<DeliveryResult<string, string>> ProduceAsync(TopicPartition topicPartition, Message<string, string> message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void Produce(string topic, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void Produce(TopicPartition topicPartition, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public int Poll(TimeSpan timeout) => 0;

        public int Flush(TimeSpan timeout) => 0;

        public void Flush(CancellationToken cancellationToken = default)
        {
        }

        public void InitTransactions(TimeSpan timeout)
        {
        }

        public void BeginTransaction()
        {
        }

        public void CommitTransaction(TimeSpan timeout)
        {
        }

        public void CommitTransaction()
        {
        }

        public void AbortTransaction(TimeSpan timeout)
        {
        }

        public void AbortTransaction()
        {
        }

        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
        {
        }

        public int AddBrokers(string brokers) => 0;

        public void SetSaslCredentials(string username, string password)
        {
        }

        public Handle Handle => throw new NotSupportedException("Not exercised by these tests.");

        public string Name => "fake-producer";

        public void Dispose()
        {
        }
    }
}
