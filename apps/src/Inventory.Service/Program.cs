using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service;
using Inventory.Service.Data;
using Inventory.Service.Endpoints;
using Inventory.Service.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});
builder.Logging.AddOrdersOpenTelemetryLogging("inventory-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("inventory-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddProblemDetails();
builder.Services.AddOptions<InventoryKafkaOptions>()
    .Bind(builder.Configuration.GetSection(InventoryKafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReservationRequestedTopic), "Kafka reservation-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReservationRepliedTopic), "Kafka reservation-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRequestedTopic), "Kafka commit-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRepliedTopic), "Kafka commit-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRequestedTopic), "Kafka release-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRepliedTopic), "Kafka release-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Kafka dead-letter topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Kafka consumer group is required.")
    .ValidateOnStart();
builder.Services.AddOptions<MessageProcessingOptions>()
    .Bind(builder.Configuration.GetSection(MessageProcessingOptions.SectionName))
    .Validate(options => options.MaximumAttempts is > 0 and <= 10, "Maximum attempts must be between 1 and 10.")
    .Validate(options => options.InitialRetryDelayMilliseconds > 0, "Initial retry delay must be positive.")
    .Validate(options => options.MaximumRetryDelayMilliseconds >= options.InitialRetryDelayMilliseconds, "Maximum retry delay must not be less than the initial delay.")
    .Validate(options => options.InfrastructureRetryDelayMilliseconds > 0, "Infrastructure retry delay must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection(OutboxOptions.SectionName))
    .Validate(options => options.BatchSize is > 0 and <= 100, "Outbox batch size must be between 1 and 100.")
    .Validate(options => options.PollIntervalMilliseconds >= 100, "Outbox poll interval must be at least 100 milliseconds.")
    .Validate(options => options.MaximumRetryDelaySeconds > 0, "Outbox maximum retry delay must be positive.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Inventory")
    ?? throw new InvalidOperationException("Connection string 'Inventory' is required.");

builder.Services.AddDbContext<InventoryDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString)
        .AddNPlusOneDetection(serviceProvider.GetRequiredService<ILoggerFactory>()));

builder.Services.Configure<RetentionOptions>(options =>
{
    options.ConnectionString = connectionString;
    options.Targets =
    [
        new RetentionTarget("outbox_messages", "processed_at"),
        new RetentionTarget("inbox_messages", "processed_at")
    ];
    options.RetentionDays = builder.Configuration.GetValue("Retention:RetentionDays", 7);
});
builder.Services.AddHostedService<RetentionSweeper>();

builder.Services.AddSingleton<IProducer<string, string>>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var config = new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        ClientId = $"{options.ClientId}-{instanceId}",
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = 10_000,
        SocketTimeoutMs = 10_000
    };

    return new ProducerBuilder<string, string>(config).Build();
});
builder.Services.AddSingleton<IAdminClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var config = new AdminClientConfig { BootstrapServers = options.BootstrapServers };
    return new AdminClientBuilder(config).Build();
});
builder.Services.AddOrdersResilience();
builder.Services.AddSingleton<IInventoryEventPublisher, KafkaInventoryEventPublisher>();
builder.Services.AddSingleton<IDeadLetterPublisher, KafkaDeadLetterPublisher>();
builder.Services.AddSingleton<InventoryReservationMessageProcessor>();
builder.Services.AddScoped<IOutboxEventDispatcher, InventoryOutboxEventDispatcher>();
builder.Services.AddHostedService<OutboxPublisher<InventoryDbContext>>();
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = serviceProvider.GetRequiredService<IDeadLetterPublisher>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.ReservationRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.ReservationRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = serviceProvider.GetRequiredService<IDeadLetterPublisher>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.ReservationCommitRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.CommitRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessCommitAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = serviceProvider.GetRequiredService<IDeadLetterPublisher>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.ReservationReleaseRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.ReleaseRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessReleaseAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Inventory"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

if (args.Contains("--seed", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await InventorySeeder.SeedAsync(dbContext, CancellationToken.None);
    return;
}

app.UseExceptionHandler();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Ok(new { service = "Inventory.Service", instanceId }));
app.MapInventoryEndpoints();

await app.RunAsync();
