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
using Polly.Registry;
using System.Text.Json;
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PaymentMessageProcessorTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();
    private CachedSchemaRegistryClient _schemaRegistryClient = null!;

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionStringTask = fixture.CreateSchemaAsync(nameof(PaymentMessageProcessorTests));
        await Task.WhenAll(connectionStringTask, _redpanda.StartAsync());
        var connectionString = await connectionStringTask;

        _schemaRegistryClient = new CachedSchemaRegistryClient(
            new SchemaRegistryConfig { Url = _redpanda.GetSchemaRegistryAddress() });

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(connectionString));
        services.Configure<PaymentRiskOptions>(_ => { });
        services.AddScoped<PaymentRiskEvaluator>();
        services.AddScoped<PaymentDecisionCoordinator>();
        services.AddOrdersResilience();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    public void Dispose()
    {
        _schemaRegistryClient.Dispose();
    }

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

    [Fact]
    public async Task ChoreographyAndOrchestrationReuseOnePrimaryPaymentForTheOrder()
    {
        var orderId = Guid.NewGuid();
        var correlationId = "cross-path-correlation";

        var choreographyResult = await CreateProcessor().ProcessAsync(
            await CreateConsumeResultAsync(Guid.NewGuid(), orderId, 49.90m),
            CancellationToken.None);

        var request = new PaymentDecisionRequested(
            orderId,
            49.90m,
            "BRL",
            correlationId,
            DateTimeOffset.UtcNow,
            CustomerId: "integration-customer");
        var orchestrationResult = await CreateDecisionProcessor().ProcessAsync(
            new ConsumeResult<string, string>
            {
                Topic = "payments.decision-requested.v1",
                Partition = new Partition(0),
                Offset = new Offset(1),
                Message = new Message<string, string>
                {
                    Key = orderId.ToString("N"),
                    Value = JsonSerializer.Serialize(request),
                    Headers = new Headers()
                }
            },
            CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, choreographyResult);
        Assert.Equal(MessageProcessingResult.Processed, orchestrationResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync(item => item.OrderId == orderId);
        Assert.True(payment.IsPrimary);
        Assert.Equal(2, await dbContext.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ReusingAnOrderIdWithDifferentPaymentInputIsRejected()
    {
        var orderId = Guid.NewGuid();
        await CreateProcessor().ProcessAsync(
            await CreateConsumeResultAsync(Guid.NewGuid(), orderId, 49.90m),
            CancellationToken.None);

        var conflictingRequest = new PaymentDecisionRequested(
            orderId,
            99.90m,
            "BRL",
            "conflicting-correlation",
            DateTimeOffset.UtcNow,
            CustomerId: "integration-customer");

        await Assert.ThrowsAsync<PaymentDecisionConflictException>(() =>
            CreateDecisionProcessor().ProcessAsync(
                new ConsumeResult<string, string>
                {
                    Topic = "payments.decision-requested.v1",
                    Partition = new Partition(0),
                    Offset = new Offset(2),
                    Message = new Message<string, string>
                    {
                        Key = orderId.ToString("N"),
                        Value = JsonSerializer.Serialize(conflictingRequest),
                        Headers = new Headers()
                    }
                },
                CancellationToken.None));

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Single(await dbContext.Payments.ToListAsync());
        Assert.Single(await dbContext.OutboxMessages.ToListAsync());
    }

    private PaymentMessageProcessor CreateProcessor()
    {
        return new PaymentMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _schemaRegistryClient,
            Options.Create(new PaymentsKafkaOptions()),
            NullLogger<PaymentMessageProcessor>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());
    }

    private PaymentDecisionRequestProcessor CreateDecisionProcessor() =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PaymentDecisionRequestOptions()),
            NullLogger<PaymentDecisionRequestProcessor>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());

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
