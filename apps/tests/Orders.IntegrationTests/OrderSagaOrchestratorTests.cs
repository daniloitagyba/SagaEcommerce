using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

/// <summary>
/// Milestone 75: proves the property the whole milestone exists for - an
/// order with line items reaching the orchestrator actually results in a
/// real inventory reservation being requested (the real SKU, persisted and
/// published), not just an order that quietly confirms because nothing
/// downstream ever checked stock. Choreography alone never exercises this
/// path at all (OrderMessageProcessor has no inventory step); this is what
/// Saga:Mode=Both is buying.
/// </summary>
public sealed class OrderSagaOrchestratorTests : IAsyncLifetime, IDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    private CachedSchemaRegistryClient _schemaRegistryClient = null!;
    private NpgsqlDataSource _dataSource = null!;
    private SagaOrchestrationStore _store = null!;
    private IProducer<string, string> _producer = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());

        var options = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var context = new OrdersDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        _store = new SagaOrchestrationStore(_dataSource, pipelineProvider);
        _schemaRegistryClient = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = _redpanda.GetSchemaRegistryAddress() });
        _producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _redpanda.GetBootstrapAddress() }).Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public void Dispose()
    {
        _producer.Dispose();
        _schemaRegistryClient.Dispose();
    }

    [Fact]
    public async Task AnOrderWithLineItemsGetsARealReservationRequestedAndPublished()
    {
        var orchestrator = CreateOrchestrator();
        var orderId = Guid.NewGuid();
        var consumeResult = await CreateConsumeResultAsync(
            orderId,
            [new OrderCreatedLine("SKU-INTEG-001", "Integration Widget", 3, 49.90m, 149.70m)]);

        await orchestrator.RequestReservationAsync(consumeResult, CancellationToken.None);

        // The property M75 is about: a real reservation, for the real SKU
        // and quantity the customer ordered, not a hash-derived stand-in
        // and not silence.
        await using var scope = _dataSource.CreateConnection();
        await scope.OpenAsync();
        await using var command = scope.CreateCommand();
        command.CommandText = "SELECT sku, quantity FROM saga_orchestration_lines WHERE order_id = @order_id";
        command.Parameters.AddWithValue("order_id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected a saga_orchestration_lines row for this order");
        Assert.Equal("SKU-INTEG-001", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        await reader.CloseAsync();

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.GetBootstrapAddress(),
            GroupId = $"test-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe("inventory.reservation-requested.v1");
        var published = consumer.Consume(TimeSpan.FromSeconds(10));

        Assert.NotNull(published);
        Assert.Equal("SKU-INTEG-001", published.Message.Key);
        Assert.Contains("\"quantity\":3", published.Message.Value);
    }

    [Fact]
    public async Task AMultiLineOrderGetsAReservationForEveryLineNotJustTheLargest()
    {
        // Milestone 78: before this, only the largest line by value got a
        // real reservation - the other lines were never checked against
        // stock at all. This is the regression test that would have caught that.
        var orchestrator = CreateOrchestrator();
        var orderId = Guid.NewGuid();
        var consumeResult = await CreateConsumeResultAsync(
            orderId,
            [
                new OrderCreatedLine("SKU-MULTI-BIG", "Big Line", 1, 500.00m, 500.00m),
                new OrderCreatedLine("SKU-MULTI-SMALL", "Small Line", 5, 10.00m, 50.00m)
            ]);

        await orchestrator.RequestReservationAsync(consumeResult, CancellationToken.None);

        await using var scope = _dataSource.CreateConnection();
        await scope.OpenAsync();
        await using var command = scope.CreateCommand();
        command.CommandText = "SELECT sku, quantity, line_index FROM saga_orchestration_lines WHERE order_id = @order_id ORDER BY line_index";
        command.Parameters.AddWithValue("order_id", orderId);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("SKU-MULTI-BIG", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SKU-MULTI-SMALL", reader.GetString(0));
        Assert.Equal(5, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync(), "expected exactly two line rows, not just the largest");
        await reader.CloseAsync();

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.GetBootstrapAddress(),
            GroupId = $"test-verify-multi-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe("inventory.reservation-requested.v1");

        var publishedKeys = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            var published = consumer.Consume(TimeSpan.FromSeconds(10));
            Assert.NotNull(published);
            publishedKeys.Add(published.Message.Key);
        }

        Assert.Equal(["SKU-MULTI-BIG", "SKU-MULTI-SMALL"], publishedKeys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnAmountOnlyOrderWithNoLinesIsSkippedRatherThanGivenAFakeReservation()
    {
        var orchestrator = CreateOrchestrator();
        var orderId = Guid.NewGuid();
        var consumeResult = await CreateConsumeResultAsync(orderId, []);

        await orchestrator.RequestReservationAsync(consumeResult, CancellationToken.None);

        await using var scope = _dataSource.CreateConnection();
        await scope.OpenAsync();
        await using var command = scope.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM saga_orchestration_states WHERE order_id = @order_id";
        command.Parameters.AddWithValue("order_id", orderId);
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, count);
    }

    private OrderSagaOrchestrator CreateOrchestrator() =>
        new(
            Options.Create(new SagaOrchestrationOptions()),
            _producer,
            _schemaRegistryClient,
            _store,
            NullLogger<OrderSagaOrchestrator>.Instance);

    private async Task<ConsumeResult<string, byte[]>> CreateConsumeResultAsync(Guid orderId, IReadOnlyList<OrderCreatedLine> lines)
    {
        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            orderId,
            "integration-customer",
            lines.Count == 0 ? 49.90m : lines.Sum(line => line.LineTotal),
            "BRL",
            DateTimeOffset.UtcNow,
            "integration-correlation",
            lines.Count == 0 ? null : lines);

        var record = OrderCreatedAvroSchema.ToGenericRecord(orderCreated);
        var serializer = new AvroSerializer<GenericRecord>(_schemaRegistryClient, new AvroSerializerConfig { AutoRegisterSchemas = true });
        var context = new SerializationContext(MessageComponentType.Value, "orders.created.v1");
        var value = await serializer.SerializeAsync(record, context);

        return new ConsumeResult<string, byte[]>
        {
            Topic = "orders.created.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, byte[]> { Key = orderId.ToString("N"), Value = value, Headers = new Headers() }
        };
    }
}
