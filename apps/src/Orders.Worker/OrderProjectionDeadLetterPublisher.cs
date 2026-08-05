using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public interface IOrderProjectionDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class OrderProjectionDeadLetterPublisher(IProducer<string, string> producer, IOptions<OrderProjectionOptions> options)
    : KafkaDeadLetterPublisherBase<byte[]>(producer, options.Value.DeadLetterTopic, "orders_projection.dead_letter.publish", Convert.ToBase64String),
        IOrderProjectionDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
