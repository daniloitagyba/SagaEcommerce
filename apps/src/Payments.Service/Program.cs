using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service;
using Payments.Service.Data;
using Payments.Service.Messaging;
using Payments.Service.Risk;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

// Milestone 69: fail loudly at startup instead of silently at runtime.
//
// A missing DI registration used to surface only when something first
// tried to resolve it - and when that something is the outbox dispatcher,
// the failure is a background loop logging an exception every poll while
// the service reports healthy and quietly stops publishing every event.
// ValidateOnBuild turns that into a refusal to start. It is off by default
// outside Development; the cost is a slower boot, which is the right trade
// against an outbox that looks fine and delivers nothing.
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
builder.Logging.AddOrdersOpenTelemetryLogging("payments-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("payments-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddProblemDetails();
builder.Services.AddOptions<PaymentsKafkaOptions>()
    .Bind(builder.Configuration.GetSection(PaymentsKafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Kafka order-created topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PaymentResultTopic), "Kafka payment-result topic is required.")
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
// Milestone 66: replaces PaymentDecisionOptions' single amount threshold
// with a scored risk policy - see PaymentRiskEvaluator.
builder.Services.AddOptions<PaymentRiskOptions>()
    .Bind(builder.Configuration.GetSection(PaymentRiskOptions.SectionName))
    .Validate(options => options.DeclineScoreThreshold > 0, "Decline score threshold must be positive.")
    .Validate(options => options.HighValueAmount > 0, "High-value amount must be positive.")
    .Validate(options => options.VelocityWindowMinutes > 0, "Velocity window must be positive.")
    .Validate(options => options.VelocityOrderThreshold > 0, "Velocity order threshold must be positive.")
    .Validate(options => options.AtypicalAmountMultiplier > 1m, "Atypical amount multiplier must be greater than one.")
    .ValidateOnStart();
builder.Services.AddOptions<PaymentSettlementOptions>()
    .Bind(builder.Configuration.GetSection(PaymentSettlementOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CaptureRequestedTopic), "Capture-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.VoidRequestedTopic), "Void-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.RefundRequestedTopic), "Refund-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.SettlementRepliedTopic), "Settlement-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Settlement dead-letter topic is required.")
    .Validate(options => options.ExpirySweepIntervalSeconds > 0, "Expiry sweep interval must be positive.")
    .Validate(options => options.ExpirySweepBatchSize > 0, "Expiry sweep batch size must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<PaymentDecisionRequestOptions>()
    .Bind(builder.Configuration.GetSection(PaymentDecisionRequestOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DecisionRequestedTopic), "Decision-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DecisionRepliedTopic), "Decision-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Decision-request dead-letter topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Kafka consumer group is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Connection string 'Payments' is required.");

builder.Services.AddDbContext<PaymentsDbContext>((serviceProvider, options) =>
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
    var options = serviceProvider.GetRequiredService<IOptions<PaymentsKafkaOptions>>().Value;
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
    var options = serviceProvider.GetRequiredService<IOptions<PaymentsKafkaOptions>>().Value;
    var config = new AdminClientConfig { BootstrapServers = options.BootstrapServers };
    return new AdminClientBuilder(config).Build();
});
builder.Services.AddOrdersResilience();
builder.Services.AddOrdersSchemaRegistry(builder.Configuration);
builder.Services.AddSingleton<IPaymentEventPublisher, KafkaPaymentEventPublisher>();
builder.Services.AddSingleton<IPaymentDecisionReplyPublisher, KafkaPaymentDecisionReplyPublisher>();
builder.Services.AddSingleton<IPaymentSettlementPublisher, KafkaPaymentSettlementPublisher>();
builder.Services.AddSingleton<IPaymentSettlementDeadLetterPublisher, PaymentSettlementDeadLetterPublisher>();
builder.Services.AddSingleton<PaymentSettlementProcessor>();
builder.Services.AddSingleton<IDeadLetterPublisher, KafkaDeadLetterPublisher>();
builder.Services.AddSingleton<IPaymentDecisionDeadLetterPublisher, PaymentDecisionDeadLetterPublisher>();
builder.Services.AddScoped<PaymentRiskEvaluator>();
builder.Services.AddSingleton<PaymentMessageProcessor>();
builder.Services.AddSingleton<PaymentDecisionRequestProcessor>();
builder.Services.AddScoped<IOutboxEventDispatcher, PaymentOutboxEventDispatcher>();
builder.Services.AddHostedService<OutboxPublisher<PaymentsDbContext>>();

// Milestone 65: which saga(s) this instance actually answers to - see
// SagaMode's own comment for why both sides needed to reach the same
// reliability bar before this toggle could mean anything. Both is for
// side-by-side comparison against identical traffic; Choreography stays
// the default so anyone not opting in keeps today's behavior.
var sagaMode = builder.Configuration.GetValue("Saga:Mode", SagaMode.Choreography);

if (sagaMode is SagaMode.Choreography or SagaMode.Both)
{
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<PaymentsKafkaOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<PaymentMessageProcessor>();
        var deadLetterPublisher = serviceProvider.GetRequiredService<IDeadLetterPublisher>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Payments.Service.OrderCreatedConsumer");
        return new KafkaConsumerHost<byte[]>(
            options.BootstrapServers, options.ConsumerGroup, options.ClientId,
            [options.OrderCreatedTopic], options.DeadLetterTopic,
            processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
    });
}

if (sagaMode is SagaMode.Orchestration or SagaMode.Both)
{
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<PaymentDecisionRequestOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<PaymentDecisionRequestProcessor>();
        var deadLetterPublisher = serviceProvider.GetRequiredService<IPaymentDecisionDeadLetterPublisher>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Payments.Service.PaymentDecisionRequestConsumer");
        return new KafkaConsumerHost<string>(
            options.BootstrapServers, options.ConsumerGroup, options.ClientId,
            [options.DecisionRequestedTopic], options.DeadLetterTopic,
            processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
    });
}

// Milestone 68: capture/void handling and the expiry sweeper run
// regardless of Saga:Mode - they follow the order's lifecycle, not the
// saga style that created it.
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PaymentSettlementOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<PaymentSettlementProcessor>();
    var deadLetterPublisher = serviceProvider.GetRequiredService<IPaymentSettlementDeadLetterPublisher>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Payments.Service.PaymentSettlementConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.CaptureRequestedTopic, options.VoidRequestedTopic, options.RefundRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddHostedService<PaymentAuthorizationSweeper>();

builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Payments"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await dbContext.Database.MigrateAsync();
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
app.MapGet("/", () => Results.Ok(new { service = "Payments.Service", instanceId }));

await app.RunAsync();
