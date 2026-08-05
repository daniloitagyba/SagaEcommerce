using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public interface IPaymentResultDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class PaymentResultDeadLetterPublisher(IProducer<string, string> producer, IOptions<PaymentResultKafkaOptions> options)
    : KafkaDeadLetterPublisherBase<string>(producer, options.Value.DeadLetterTopic, "payments_result.dead_letter.publish", value => value ?? string.Empty),
        IPaymentResultDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
