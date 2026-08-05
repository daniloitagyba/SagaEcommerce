using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

public interface IDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class KafkaDeadLetterPublisher(IProducer<string, string> producer, IOptions<InventoryKafkaOptions> options)
    : KafkaDeadLetterPublisherBase<string>(producer, options.Value.DeadLetterTopic, "inventory.dead_letter.publish", value => value ?? string.Empty),
        IDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
