using BuildingBlocks;
using BuildingBlocks.WebAuthentication;
using Confluent.Kafka;
using Inventory.Service;
using Inventory.Service.Data;
using Inventory.Service.Endpoints;
using Inventory.Service.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Same guard Orders.Api already carries, where
// one unregistered IProducer took the whole outbox down while the service
// went on reporting healthy - a background loop cannot fail loudly on its
// own, so the failure has to happen at startup instead.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});
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
    .Validate(options => !string.IsNullOrWhiteSpace(options.RestockRequestedTopic), "Kafka restock-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.RestockRepliedTopic), "Kafka restock-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRequestedTopic), "Kafka commit-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRepliedTopic), "Kafka commit-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRequestedTopic), "Kafka release-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRepliedTopic), "Kafka release-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.BackorderCancellationRequestedTopic), "Kafka backorder-cancellation-requested topic is required.")
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
    .Validate(options => options.MaximumAttempts > 0, "Outbox maximum attempts must be positive.")
    .Validate(options => options.MaxConcurrentPublishes > 0, "Outbox max concurrent publishes must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<BackorderOptions>()
    .Bind(builder.Configuration.GetSection(BackorderOptions.SectionName))
    .Validate(options => options.TimeoutSweepIntervalSeconds > 0, "Backorder timeout sweep interval must be positive.")
    .Validate(options => options.TimeoutSweepBatchSize > 0, "Backorder timeout sweep batch size must be positive.")
    .Validate(options => options.TimeoutMinutes > 0, "Backorder timeout window must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<ReplenishmentOptions>()
    .Bind(builder.Configuration.GetSection(ReplenishmentOptions.SectionName))
    .Validate(options => options.LeadTimeSeconds > 0, "Replenishment lead time must be positive.")
    .Validate(options => options.ReceivingSweepIntervalSeconds > 0, "Replenishment receiving sweep interval must be positive.")
    .Validate(options => options.ReceivingSweepBatchSize > 0, "Replenishment receiving sweep batch size must be positive.")
    .Validate(options => options.TargetMultiplier > 1, "Replenishment target multiplier must be greater than one.")
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
        new RetentionTarget("inbox_messages", "processed_at"),
        // A committed reservation stops being useful audit evidence once
        // both the anti-entropy sweep (a cancelled-order divergence is
        // caught within one sweep interval of occurring, not months later)
        // and the return window (7 days, ReturnOptions.RegretWindowDays)
        // have had every realistic chance to act on it. 60 days is
        // generous on both counts, not a measured number - unlike
        // outbox/inbox, there is no load-test data yet for how fast this
        // table actually grows.
        new RetentionTarget("inventory_reservation_ledger", "committed_at", RetentionDaysOverride: 60)
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IInventoryEventPublisher, KafkaInventoryEventPublisher>();
builder.Services.AddScoped<WarehouseAllocationStore>();
builder.Services.AddSingleton<InventoryReservationMessageProcessor>();
builder.Services.AddScoped<IOutboxEventDispatcher, InventoryOutboxEventDispatcher>();
builder.Services.AddHostedService<OutboxPublisher<InventoryDbContext>>();
builder.Services.AddHostedService<BackorderTimeoutSweeper>();
builder.Services.AddSingleton<ReplenishmentRequestProcessor>();
builder.Services.AddHostedService<PurchaseOrderReceivingSweeper>();
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
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
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
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
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.ReservationReleaseRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.ReleaseRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessReleaseAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.RestockRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.RestockRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessRestockAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<InventoryReservationMessageProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.BackorderCancellationRequestedConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.BackorderCancellationRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessBackorderCancellationAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<InventoryKafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<ReplenishmentRequestProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "inventory.dead_letter.publish",
        value => value ?? string.Empty);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Service.ReplenishmentNeededConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.ReplenishmentNeededTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});
// GET /inventory (exact quantities, whole catalog) was
// unauthenticated - commercially sensitive sell-through data for anyone
// who could reach this pod. Same JWKS-backed validation as Orders.Api.
builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "inventory-service");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("inventory:read", policy => policy.RequireRole("inventory:read"));

// KafkaHealthCheck must be a singleton, not the transient default
// AddCheck<T> would otherwise register - see that class's own comment.
builder.Services.AddSingleton<KafkaHealthCheck>();
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Inventory"])
    // Not "ready" - this service also serves GET /inventory/backorders and
    // GET /inventory/committed-reservations over plain HTTP for Orders.Worker's
    // anti-entropy sweep, reads that have nothing to do with Kafka. See
    // Payments.Service's identical reasoning and
    // docs/roadmap-milestones-91-99.md, "readiness is gated on
    // dependencies the code deliberately treats as optional".
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["live-dependencies"]);

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
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live-dependencies")
});
app.MapGet("/", () => Results.Ok(new { service = "Inventory.Service", instanceId }));
app.MapInventoryEndpoints();

await app.RunAsync();
