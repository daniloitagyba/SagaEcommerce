using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public interface IOrderEventStoreDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class OrderEventStoreDeadLetterPublisher(IProducer<string, string> producer, IOptions<OrderEventStoreOptions> options)
    : KafkaDeadLetterPublisherBase<byte[]>(producer, options.Value.DeadLetterTopic, "orders_event_store.dead_letter.publish", Convert.ToBase64String),
        IOrderEventStoreDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
