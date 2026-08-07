using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Service;
using Payments.Service.Data;
using Payments.Service.Risk;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

public sealed class PaymentMessageProcessorTests : IAsyncLifetime, IDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("payments_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    // Confluent.SchemaRegistry 2.15.0 ships no mock/in-memory client, and
    // ISchemaRegistryClient has 24+ members - hand-rolling a fake risks
    // subtly wrong behavior around schema IDs. Redpanda bundles a
    // Confluent-compatible schema registry in the same single container as
    // its Kafka-API broker, so this gets a real, ephemeral, hermetic
    // registry per test run instead of depending on a specific host's
    // always-on Karapace instance (the previous approach, fine on a single
    // personal server but unreachable from CI runners).
    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();
    private CachedSchemaRegistryClient _schemaRegistryClient = null!;

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());

        _schemaRegistryClient = new CachedSchemaRegistryClient(
            new SchemaRegistryConfig { Url = _redpanda.GetSchemaRegistryAddress() });

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        // Milestone 66: the processor resolves the risk evaluator per
        // message from its own scope, so it has to be registered here too.
        services.Configure<PaymentRiskOptions>(_ => { });
        services.AddScoped<PaymentRiskEvaluator>();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public void Dispose()
    {
        _schemaRegistryClient.Dispose();
    }

    // Milestone 66 replaced the bare amount threshold with a scored risk
    // policy, and these two cases land the same way for a better reason:
    // 49.90 from an unseen customer scores FIRST_PURCHASE(20), under the
    // 60 decline threshold; 5000.00 scores HIGH_VALUE(50) +
    // FIRST_PURCHASE(20) = 70, over it. The outcome is unchanged, so this
    // still guards the same behaviour it always did.
    [Theory]
    [InlineData(49.90, true)]
    [InlineData(5000.00, false)]
    public async Task ProcessAsyncDecidesBasedOnRiskScore(decimal amount, bool expectedApproved)
    {
        var processor = CreateProcessor();
        var consumeResult = await CreateConsumeResultAsync(Guid.NewGuid(), Guid.NewGuid(), amount);

        var result = await processor.ProcessAsync(consumeResult, CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();

        Assert.Equal(expectedApproved, payment.Approved);
        Assert.Equal(nameof(PaymentDecided), outboxMessage.EventType);
    }

    [Fact]
    public async Task ProcessAsyncSkipsDuplicateEvents()
    {
        var processor = CreateProcessor();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var firstResult = await processor.ProcessAsync(await CreateConsumeResultAsync(eventId, orderId, 49.90m), CancellationToken.None);
        var secondResult = await processor.ProcessAsync(await CreateConsumeResultAsync(eventId, orderId, 49.90m), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, firstResult);
        Assert.Equal(MessageProcessingResult.Duplicate, secondResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Equal(1, await dbContext.Payments.CountAsync());
    }

    private PaymentMessageProcessor CreateProcessor()
    {
        return new PaymentMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _schemaRegistryClient,
            Options.Create(new PaymentsKafkaOptions()),
            Options.Create(new PaymentRiskOptions()),
            NullLogger<PaymentMessageProcessor>.Instance);
    }

    private async Task<ConsumeResult<string, byte[]>> CreateConsumeResultAsync(Guid eventId, Guid orderId, decimal amount)
    {
        var orderCreated = new OrderCreated(
            eventId,
            orderId,
            "integration-customer",
            amount,
            "BRL",
            DateTimeOffset.UtcNow,
            "integration-correlation");

        var record = OrderCreatedAvroSchema.ToGenericRecord(orderCreated);
        var serializer = new AvroSerializer<GenericRecord>(_schemaRegistryClient, new AvroSerializerConfig { AutoRegisterSchemas = true });
        var context = new SerializationContext(MessageComponentType.Value, "orders.created.v1");
        var value = await serializer.SerializeAsync(record, context);

        return new ConsumeResult<string, byte[]>
        {
            Topic = "orders.created.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, byte[]>
            {
                Key = orderId.ToString("N"),
                Value = value,
                Headers = new Headers()
            }
        };
    }
}
