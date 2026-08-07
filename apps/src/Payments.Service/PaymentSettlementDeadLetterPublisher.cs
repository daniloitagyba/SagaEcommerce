using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Payments.Service;

public interface IPaymentSettlementDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class PaymentSettlementDeadLetterPublisher(IProducer<string, string> producer, IOptions<PaymentSettlementOptions> options)
    : KafkaDeadLetterPublisherBase<string>(producer, options.Value.DeadLetterTopic, "payments.settlement.dead_letter.publish", payload => payload),
        IPaymentSettlementDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
