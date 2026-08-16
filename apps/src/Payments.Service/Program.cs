using BuildingBlocks;
using BuildingBlocks.WebAuthentication;
using Confluent.Kafka;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service;
using Payments.Service.Data;
using Payments.Service.Messaging;
using Payments.Service.Risk;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
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
    .Validate(options => options.MaximumAttempts > 0, "Outbox maximum attempts must be positive.")
    .Validate(options => options.MaxConcurrentPublishes > 0, "Outbox max concurrent publishes must be positive.")
    .ValidateOnStart();
// Replaces PaymentDecisionOptions' single amount threshold
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
    .Validate(options => !string.IsNullOrWhiteSpace(options.RefundRequestedTopic), "Refund-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CancellationRequestedTopic), "Cancellation-requested topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.SettlementRepliedTopic), "Settlement-replied topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeadLetterTopic), "Settlement dead-letter topic is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroup), "Settlement consumer group is required.")
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
builder.Services.AddSingleton<PaymentSettlementProcessor>();
builder.Services.AddScoped<PaymentRiskEvaluator>();
builder.Services.AddScoped<PaymentDecisionCoordinator>();
builder.Services.AddSingleton<PaymentMessageProcessor>();
builder.Services.AddSingleton<PaymentDecisionRequestProcessor>();
builder.Services.AddScoped<IOutboxEventDispatcher, PaymentOutboxEventDispatcher>();
builder.Services.AddHostedService<OutboxPublisher<PaymentsDbContext>>();

// Orchestration is the safe production default because it includes the
// inventory reservation step. Both remains available for isolated
// comparisons, while PaymentDecisionCoordinator prevents a comparison from
// creating two independently settleable payments for one order.
var sagaMode = builder.Configuration.GetValue("Saga:Mode", SagaMode.Orchestration);

if (sagaMode is SagaMode.Choreography or SagaMode.Both)
{
    builder.Services.AddSingleton<IHostedService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<PaymentsKafkaOptions>>().Value;
        var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
        var processor = serviceProvider.GetRequiredService<PaymentMessageProcessor>();
        var deadLetterPublisher = new KafkaDeadLetterPublisher<byte[]>(
            serviceProvider.GetRequiredService<IProducer<string, string>>(),
            options.DeadLetterTopic,
            "payments.dead_letter.publish",
            Convert.ToBase64String);
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
        var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
            serviceProvider.GetRequiredService<IProducer<string, string>>(),
            options.DeadLetterTopic,
            "payments.decision_request.dead_letter.publish",
            payload => payload);
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Payments.Service.PaymentDecisionRequestConsumer");
        return new KafkaConsumerHost<string>(
            options.BootstrapServers, options.ConsumerGroup, options.ClientId,
            [options.DecisionRequestedTopic], options.DeadLetterTopic,
            processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
    });
}

// Capture/void handling and the expiry sweeper run
// regardless of Saga:Mode - they follow the order's lifecycle, not the
// saga style that created it.
builder.Services.AddSingleton<IHostedService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PaymentSettlementOptions>>().Value;
    var processingOptions = serviceProvider.GetRequiredService<IOptions<MessageProcessingOptions>>().Value;
    var processor = serviceProvider.GetRequiredService<PaymentSettlementProcessor>();
    var deadLetterPublisher = new KafkaDeadLetterPublisher<string>(
        serviceProvider.GetRequiredService<IProducer<string, string>>(),
        options.DeadLetterTopic,
        "payments.settlement.dead_letter.publish",
        payload => payload);
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Payments.Service.PaymentSettlementConsumer");
    return new KafkaConsumerHost<string>(
        options.BootstrapServers, options.ConsumerGroup, options.ClientId,
        [options.CaptureRequestedTopic, options.RefundRequestedTopic, options.CancellationRequestedTopic], options.DeadLetterTopic,
        processingOptions, processor.ProcessAsync, deadLetterPublisher.PublishAsync, logger);
});
builder.Services.AddHostedService<PaymentAuthorizationSweeper>();

// The same JWKS-backed wiring Orders.Api/Catalog/
// Inventory/Cart already carry, now shared via
// BuildingBlocks.WebAuthentication instead of copied a fifth time.
// payments:read is granted to orders-api-clients' service account, the
// same trusted-backend-tooling client Orders.Worker's anti-entropy sweeper
// authenticates as - see KeycloakTokenProvider in Orders.Worker.
builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "payments-service");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("payments:read", policy => policy.RequireRole("payments:read"));

// KafkaHealthCheck must be a singleton, not the transient default
// AddCheck<T> would otherwise register - see that class's own comment.
builder.Services.AddSingleton<KafkaHealthCheck>();
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Payments"])
    // Not "ready" - this service also serves GET /payments/by-order/{id}
    // over plain HTTP for Orders.Worker's anti-entropy sweep, a read that
    // has nothing to do with Kafka. Gating readiness on Kafka used to
    // remove this pod from the Service the moment Kafka had a blip -
    // taking the one read anti-entropy needs to detect a payment
    // divergence out with it, right when the sweep needed it most. See
    // docs/roadmap-milestones-91-99.md, "readiness is gated on
    // dependencies the code deliberately treats as optional".
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["live-dependencies"]);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
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
app.MapGet("/", () => Results.Ok(new { service = "Payments.Service", instanceId }));

// The one read this service otherwise has no reason to
// serve - Payments is Kafka-only everywhere else, but Orders.Worker's
// anti-entropy sweeper needs to ask "what does Payments think happened to
// this order" without a second copy of payment state living in Orders'
// own database. payments:read-gated, the same
// JWKS-backed protection already given to every other
// cross-service surface - closed, not just named, now that Orders.Worker
// has a service identity (KeycloakTokenProvider) to present.
app.MapGet("/payments/by-order/{orderId:guid}", async (Guid orderId, PaymentsDbContext dbContext, CancellationToken cancellationToken) =>
{
    var payment = await dbContext.Payments
        .AsNoTracking()
        .SingleOrDefaultAsync(item => item.IsPrimary && item.OrderId == orderId, cancellationToken);

    return payment is null
        ? Results.NotFound()
        : Results.Ok(new { payment.OrderId, payment.State, payment.Amount, payment.Currency, payment.DecidedAt });
}).WithTags("Payments").RequireAuthorization("payments:read");

// The batch counterpart of the single-order lookup above - introduced at
// Milestone 91 for Orders.Worker's anti-entropy sweep, which used to issue
// one GET per candidate order (up to AntiEntropy:BatchSize of them,
// sequentially) every tick just to check whether each one has an
// accounted payment. One query here replaces that whole loop. Capped at
// 500 ids per call, matching AntiEntropyOptions.BatchSize's own upper
// bound in practice - this is an internal, service-to-service endpoint,
// not a public one, so the cap exists to bound one query's own cost, not
// to defend against an untrusted caller.
app.MapPost("/payments/by-orders", async (IReadOnlyList<Guid> orderIds, PaymentsDbContext dbContext, CancellationToken cancellationToken) =>
{
    if (orderIds.Count == 0)
    {
        return Results.Ok(Array.Empty<object>());
    }

    if (orderIds.Count > 500)
    {
        return Results.BadRequest(new { detail = "At most 500 order ids are allowed per request." });
    }

    var payments = await dbContext.Payments
        .AsNoTracking()
        .Where(item => item.IsPrimary && orderIds.Contains(item.OrderId))
        .Select(item => new { item.OrderId, item.State, item.Amount, item.Currency, item.DecidedAt })
        .ToListAsync(cancellationToken);

    return Results.Ok(payments);
}).WithTags("Payments").RequireAuthorization("payments:read");

await app.RunAsync();
