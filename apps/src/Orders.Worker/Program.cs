using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Worker;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

// Fail loudly at startup instead of silently at runtime. A
// missing DI registration used to surface only when first resolved - if
// that's the outbox dispatcher, it's a background loop logging an
// exception every poll while the service reports healthy and delivers
// nothing. ValidateOnBuild trades a slower boot for refusing to start instead.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});
builder.Logging.AddOrdersOpenTelemetryLogging("orders-worker", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("orders-worker", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Kafka topic is required.")
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
builder.Services.AddOptions<PaymentResultKafkaOptions>()
    .Bind(builder.Configuration.GetSection(PaymentResultKafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentResultTopic), "Payment result topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Kafka dead-letter topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Kafka consumer group is required.")
    .ValidateOnStart();
builder.Services.AddOptions<OrderProjectionOptions>()
    .Bind(builder.Configuration.GetSection(OrderProjectionOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Order-created topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentResultTopic), "Payment result topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Kafka dead-letter topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Kafka consumer group is required.")
    .ValidateOnStart();
builder.Services.AddOptions<SagaOrchestrationOptions>()
    .Bind(builder.Configuration.GetSection(SagaOrchestrationOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Order-created topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DecisionRequestedTopic), "Decision-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DecisionRepliedTopic), "Decision-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReservationRequestedTopic), "Reservation-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReservationRepliedTopic), "Reservation-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRequestedTopic), "Commit-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CommitRepliedTopic), "Commit-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRequestedTopic), "Release-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReleaseRepliedTopic), "Release-replied topic is required.")
    .Validate(options => options.TimeoutSeconds > 0, "Saga timeout must be positive.")
    .Validate(options => options.OutboxBatchSize is > 0 and <= 100, "Saga outbox batch size must be between 1 and 100.")
    .Validate(options => options.OutboxPollIntervalMilliseconds >= 100, "Saga outbox poll interval must be at least 100 milliseconds.")
    .Validate(options => options.OutboxMaximumRetryDelaySeconds > 0, "Saga outbox maximum retry delay must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<OrderEventStoreOptions>()
    .Bind(builder.Configuration.GetSection(OrderEventStoreOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Order-created topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentResultTopic), "Payment result topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Kafka consumer group is required.")
    .ValidateOnStart();
builder.Services.AddOptions<LeaderElectionOptions>()
    .Bind(builder.Configuration.GetSection(LeaderElectionOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Namespace), "Leader election namespace is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.LeaseName), "Leader election lease name is required.")
    .Validate(options => options.LeaseDurationSeconds > options.RenewDeadlineSeconds, "Lease duration must exceed the renew deadline.")
    .ValidateOnStart();
builder.Services.AddOptions<CatalogClientOptions>()
    .Bind(builder.Configuration.GetSection(CatalogClientOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Catalog base URL is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Connection string 'Orders' is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RetentionOptions>(options =>
{
    options.ConnectionString = connectionString;
    options.Targets =
    [
        new RetentionTarget("outbox_messages", "processed_at"),
        new RetentionTarget("inbox_messages", "processed_at"),
        new RetentionTarget("saga_outbox_messages", "processed_at")
    ];
    options.RetentionDays = builder.Configuration.GetValue("Retention:RetentionDays", 7);
});
builder.Services.AddHostedService<RetentionSweeper>();
builder.Services.AddOrdersResilience();
builder.Services.AddOrdersSchemaRegistry(builder.Configuration);
builder.Services.AddSingleton<InboxStore>();
builder.Services.AddSingleton<PaymentSettlementRequester>();
builder.Services.AddSingleton<CouponRedemptionStore>();
builder.Services.AddSingleton<CustomerTierStore>();
builder.Services.AddSingleton<OrderStatusStore>();
builder.Services.AddSingleton<OrderMessageProcessor>();
builder.Services.AddSingleton<PaymentResultProcessor>();
builder.Services.AddSingleton<OrderProjectionStore>();
builder.Services.AddSingleton<OrderProjectionProcessor>();
builder.Services.AddOrdersRedis(builder.Configuration);
builder.Services.AddSingleton<IOrderCacheInvalidator, RedisOrderCacheInvalidator>();
builder.Services.AddSingleton<IBestsellersStore, RedisBestsellersStore>();
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CatalogClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
}).AddBestEffortHttpResilience();

// This service's own credentials - reuses
// orders-api-clients (already provisioned as this lab's trusted-backend-tooling client)
// rather than provisioning a new one, since it already carries every role
// and audience the sweep below needs.
builder.Services.AddOptions<KeycloakOptions>()
    .Bind(builder.Configuration.GetSection(KeycloakOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret), "Keycloak client secret is required.")
    .ValidateOnStart();
builder.Services.AddHttpClient<KeycloakTokenProvider>();
builder.Services.AddTransient<BearerTokenHandler>();

// The anti-entropy sweep's two cross-service reads.
// Both now authenticate as the service identity
// above - BearerTokenHandler attaches the token before the resilience
// pipeline's retries run, so a retried request is still authenticated.
builder.Services.AddOptions<AntiEntropyOptions>()
    .Bind(builder.Configuration.GetSection(AntiEntropyOptions.SectionName))
    .Validate(options => options.SweepIntervalSeconds > 0, "Anti-entropy sweep interval must be positive.")
    .Validate(options => options.BatchSize > 0, "Anti-entropy batch size must be positive.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentsBaseUrl), "Payments base URL is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.InventoryBaseUrl), "Inventory base URL is required.")
    .ValidateOnStart();
builder.Services.AddHttpClient("anti-entropy-payments", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AntiEntropyOptions>>().Value;
    client.BaseAddress = new Uri(options.PaymentsBaseUrl);
}).AddHttpMessageHandler<BearerTokenHandler>().AddBestEffortHttpResilience();
builder.Services.AddHttpClient("anti-entropy-inventory", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AntiEntropyOptions>>().Value;
    client.BaseAddress = new Uri(options.InventoryBaseUrl);
}).AddHttpMessageHandler<BearerTokenHandler>().AddBestEffortHttpResilience();
builder.Services.AddHostedService<AntiEntropySweeper>();

builder.Services.AddSingleton<IProducer<string, string>>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
    var config = new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        ClientId = $"{options.ClientId}-dlq",
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = 10_000,
        SocketTimeoutMs = 10_000
    };

    return new ProducerBuilder<string, string>(config).Build();
});
builder.Services.AddSingleton<IAdminClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
    var config = new AdminClientConfig { BootstrapServers = options.BootstrapServers };
    return new AdminClientBuilder(config).Build();
});
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<OrderMessageProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<byte[]>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "orders.dead_letter.publish",
        Convert.ToBase64String);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.OrderCreatedConsumer");
    return new KafkaConsumerHost<byte[]>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.OrderCreatedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});
// Which saga(s) this instance answers to - see SagaMode's own comment.
// Orchestration is the default (and every deployed Compose/Kubernetes
// configuration's explicit value) because it includes the inventory
// reservation step the choreographed path skips entirely; Both remains
// available for a side-by-side comparison.
var sagaMode = builder.Configuration.GetValue("Saga:Mode", SagaMode.Orchestration);

if (sagaMode is SagaMode.Choreography or SagaMode.Both)
{
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<PaymentResultKafkaOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<PaymentResultProcessor>();
        var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
            serviceProvider.GetRequiredService<IProducer<string, string>>(),
            options.DeadLetterTopic,
            "payments_result.dead_letter.publish",
            value => value ?? string.Empty);
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.PaymentResultConsumer");
        return new KafkaConsumerHost<string>(
            options.BootstrapServers, options.ConsumerGroup, options.ClientId,
            [options.PaymentResultTopic], options.DeadLetterTopic,
            processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
    });
}

builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OrderProjectionOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<OrderProjectionProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<byte[]>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "orders_projection.dead_letter.publish",
        Convert.ToBase64String);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.OrderProjectionConsumer");
    return new KafkaConsumerHost<byte[]>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.OrderCreatedTopic, options.PaymentResultTopic, options.OrderStatusChangedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});

builder.Services.AddSingleton<SagaOrchestrationStore>();

// Defaults to Kubernetes (today's real deployment target); Compose sets
// LeaderElection__Mode=SingleNode, since there's no cluster there to hold
// a Lease against and orders-worker always runs at replicas: 1 anyway -
// see LeaderElectionMode's doc comment.
var leaderElectionMode = builder.Configuration.GetValue("LeaderElection:Mode", LeaderElectionMode.Kubernetes);
if (leaderElectionMode is LeaderElectionMode.SingleNode)
{
    builder.Services.AddSingleton<ILeaderElection, SingleNodeLeaderElection>();
}
else
{
    builder.Services.AddSingleton<LeaderElectionService>();
    builder.Services.AddSingleton<ILeaderElection>(serviceProvider => serviceProvider.GetRequiredService<LeaderElectionService>());
    builder.Services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<LeaderElectionService>());
}

if (sagaMode is SagaMode.Orchestration or SagaMode.Both)
{
    builder.Services.AddSingleton<OrderSagaOrchestrator>();
    builder.Services.AddSingleton<OrderSagaReplyConsumer>();
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<SagaOrchestrationOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<OrderSagaOrchestrator>();
        // Request-side (OrderCreatedTopic, Avro/byte[]) - see the reply-side
        // consumer below for the string-valued counterpart.
        var deadLetterPublisher = new KafkaDeadLetterPublisher<byte[]>(
            serviceProvider.GetRequiredService<IProducer<string, string>>(),
            options.DeadLetterTopic,
            "orders_saga.dead_letter.publish",
            Convert.ToBase64String);
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.OrderSagaOrchestrator");
        return new KafkaConsumerHost<byte[]>(
            options.BootstrapServers, options.RequestConsumerGroup, options.ClientId,
            [options.OrderCreatedTopic], options.DeadLetterTopic,
            processingOptions, processor.RequestReservationAsync, deadLetterPublisher.PublishAsync, logger);
    });
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<SagaOrchestrationOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<OrderSagaReplyConsumer>();
        // Reply-side (the five *Replied topics, JSON/string) - see the
        // request-side consumer above for the byte[]-valued counterpart.
        var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
            serviceProvider.GetRequiredService<IProducer<string, string>>(),
            options.DeadLetterTopic,
            "orders_saga_reply.dead_letter.publish",
            value => value ?? string.Empty);
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.OrderSagaReplyConsumer");
        return new KafkaConsumerHost<string>(
            options.BootstrapServers, options.ReplyConsumerGroup, $"{options.ClientId}-reply",
            [
                options.ReservationRepliedTopic,
                options.DecisionRepliedTopic,
                options.CommitRepliedTopic,
                options.ReleaseRepliedTopic,
                options.SettlementRepliedTopic
            ],
            options.DeadLetterTopic,
            processingOptions, processor.DispatchAsync, deadLetterPublisher.PublishAsync, logger);
    });
    builder.Services.AddHostedService<SagaTimeoutSweeper>();
    builder.Services.AddHostedService<SagaOutboxPublisher>();
}

builder.Services.AddSingleton<OrderEventStoreAppender>();
builder.Services.AddSingleton<OrderEventStoreProjector>();
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OrderEventStoreOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<OrderEventStoreProjector>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<byte[]>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "orders_event_store.dead_letter.publish",
        Convert.ToBase64String);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Worker.OrderEventStoreProjector");
    return new KafkaConsumerHost<byte[]>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.OrderCreatedTopic, options.PaymentResultTopic, options.OrderStatusChangedTopic], options.DeadLetterTopic,
        processingOptions, processor.AppendAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddHealthChecks()
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Orders"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Ok(new { service = "Orders.Worker", instanceId }));

await app.RunAsync();
