using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Payments.Service;

public interface IDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class KafkaDeadLetterPublisher(IProducer<string, string> producer, IOptions<PaymentsKafkaOptions> options)
    : KafkaDeadLetterPublisherBase<byte[]>(producer, options.Value.DeadLetterTopic, "payments.dead_letter.publish", Convert.ToBase64String),
        IDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
