using Confluent.Kafka;
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

/// <summary>Proves Kafka's transactional producer/consumer API delivers exactly-once for a Kafka-to-Kafka hop; that guarantee does not extend to a Postgres write in the same logical step, which still needs Outbox+Inbox.</summary>
public sealed class KafkaTransactionsTests : IAsyncLifetime
{
    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    public async Task InitializeAsync() => await _redpanda.StartAsync();

    public async Task DisposeAsync() => await _redpanda.DisposeAsync();

    [Fact]
    public async Task NonTransactionalConsumeProduceDuplicatesOutputAfterACrashBeforeCommit()
    {
        var bootstrapServers = _redpanda.GetBootstrapAddress();
        var inputTopic = $"txn-input-{Guid.NewGuid():N}";
        var outputTopic = $"txn-output-{Guid.NewGuid():N}";
        var groupId = $"at-least-once-{Guid.NewGuid():N}";

        using (var seedProducer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build())
        {
            await seedProducer.ProduceAsync(inputTopic, new Message<string, string> { Key = "order-1", Value = "reserve-order-1" });
            seedProducer.Flush(TimeSpan.FromSeconds(5));
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        using var outputProducer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build();

        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        {
            consumer.Subscribe(inputTopic);
            var result = consumer.Consume(TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            await outputProducer.ProduceAsync(outputTopic, new Message<string, string> { Key = result.Message.Key, Value = $"processed:{result.Message.Value}" });
            outputProducer.Flush(TimeSpan.FromSeconds(5));
            consumer.Close();
        }

        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        {
            consumer.Subscribe(inputTopic);
            var result = consumer.Consume(TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            await outputProducer.ProduceAsync(outputTopic, new Message<string, string> { Key = result.Message.Key, Value = $"processed:{result.Message.Value}" });
            outputProducer.Flush(TimeSpan.FromSeconds(5));
            consumer.Commit(result);
            consumer.Close();
        }

        var outputCount = await CountMessagesAsync(bootstrapServers, outputTopic, IsolationLevel.ReadCommitted);
        Assert.Equal(2, outputCount);
    }

    [Fact]
    public async Task TransactionalConsumeProduceLeavesNoTraceOfAnAbortedCrashAttempt()
    {
        var bootstrapServers = _redpanda.GetBootstrapAddress();
        var inputTopic = $"txn-input-{Guid.NewGuid():N}";
        var outputTopic = $"txn-output-{Guid.NewGuid():N}";
        var groupId = $"exactly-once-{Guid.NewGuid():N}";
        var transactionalId = $"txn-{Guid.NewGuid():N}";

        using (var seedProducer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build())
        {
            await seedProducer.ProduceAsync(inputTopic, new Message<string, string> { Key = "order-1", Value = "reserve-order-1" });
            seedProducer.Flush(TimeSpan.FromSeconds(5));
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            IsolationLevel = IsolationLevel.ReadCommitted
        };

        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            TransactionalId = transactionalId
        }).Build())
        {
            consumer.Subscribe(inputTopic);
            producer.InitTransactions(TimeSpan.FromSeconds(10));

            var result = consumer.Consume(TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            producer.BeginTransaction();
            producer.Produce(outputTopic, new Message<string, string> { Key = result.Message.Key, Value = $"processed:{result.Message.Value}" });
            producer.SendOffsetsToTransaction(
                [new TopicPartitionOffset(result.TopicPartition, result.Offset + 1)],
                consumer.ConsumerGroupMetadata,
                TimeSpan.FromSeconds(10));
            producer.AbortTransaction();
            consumer.Close();
        }

        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            TransactionalId = $"{transactionalId}-retry"
        }).Build())
        {
            consumer.Subscribe(inputTopic);
            producer.InitTransactions(TimeSpan.FromSeconds(10));

            var result = consumer.Consume(TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            producer.BeginTransaction();
            producer.Produce(outputTopic, new Message<string, string> { Key = result.Message.Key, Value = $"processed:{result.Message.Value}" });
            producer.SendOffsetsToTransaction(
                [new TopicPartitionOffset(result.TopicPartition, result.Offset + 1)],
                consumer.ConsumerGroupMetadata,
                TimeSpan.FromSeconds(10));
            producer.CommitTransaction();
            consumer.Close();
        }

        var outputCount = await CountMessagesAsync(bootstrapServers, outputTopic, IsolationLevel.ReadCommitted);
        Assert.Equal(1, outputCount);
    }

    [Fact]
    public async Task TransactionalProduceLatencyCostVersusNonTransactional()
    {
        var bootstrapServers = _redpanda.GetBootstrapAddress();
        const int messageCount = 100;

        var plainTopic = $"latency-plain-{Guid.NewGuid():N}";
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build())
        {
            var start = DateTimeOffset.UtcNow;
            for (var i = 0; i < messageCount; i++)
            {
                await producer.ProduceAsync(plainTopic, new Message<string, string> { Key = $"k{i}", Value = $"v{i}" });
            }

            producer.Flush(TimeSpan.FromSeconds(10));
            var elapsed = DateTimeOffset.UtcNow - start;
            Console.WriteLine($"non-transactional: {messageCount} messages in {elapsed.TotalMilliseconds:F0}ms ({elapsed.TotalMilliseconds / messageCount:F2}ms/msg)");
        }

        var txnTopic = $"latency-txn-{Guid.NewGuid():N}";
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            TransactionalId = $"latency-{Guid.NewGuid():N}"
        }).Build())
        {
            producer.InitTransactions(TimeSpan.FromSeconds(10));
            var start = DateTimeOffset.UtcNow;
            for (var i = 0; i < messageCount; i++)
            {
                producer.BeginTransaction();
                producer.Produce(txnTopic, new Message<string, string> { Key = $"k{i}", Value = $"v{i}" });
                producer.CommitTransaction();
            }

            var elapsed = DateTimeOffset.UtcNow - start;
            Console.WriteLine($"transactional (1 txn/msg): {messageCount} messages in {elapsed.TotalMilliseconds:F0}ms ({elapsed.TotalMilliseconds / messageCount:F2}ms/msg)");
        }
    }

    private static async Task<int> CountMessagesAsync(string bootstrapServers, string topic, IsolationLevel isolationLevel)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"counter-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            IsolationLevel = isolationLevel
        }).Build();

        consumer.Subscribe(topic);
        var count = 0;
        while (true)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(3));
            if (result is null)
            {
                break;
            }

            count++;
        }

        consumer.Close();
        await Task.CompletedTask;
        return count;
    }
}
