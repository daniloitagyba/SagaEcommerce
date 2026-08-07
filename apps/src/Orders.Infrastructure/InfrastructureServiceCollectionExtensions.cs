using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application.Ports;
using Orders.Infrastructure.Caching;
using Orders.Infrastructure.Data;
using Orders.Infrastructure.Idempotency;
using Orders.Infrastructure.Messaging;
using Orders.Infrastructure.RateLimiting;

namespace Orders.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddOrdersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString,
        string instanceId)
    {
        services.AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName))
            .Validate(options => options.TimeToLiveSeconds > 0, "Cache time-to-live must be positive.")
            .Validate(options => options.LockTimeoutMilliseconds > 0, "Cache lock timeout must be positive.")
            .Validate(options => options.LockRetryAttempts >= 0, "Cache lock retry attempts must not be negative.")
            .Validate(options => options.LockRetryDelayMilliseconds > 0, "Cache lock retry delay must be positive.")
            .ValidateOnStart();
        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(options => options.TimeToLiveHours > 0, "Idempotency time-to-live must be positive.")
            .Validate(options => options.LockTimeoutMilliseconds > 0, "Idempotency lock timeout must be positive.")
            .Validate(options => options.LockRetryAttempts >= 0, "Idempotency lock retry attempts must not be negative.")
            .Validate(options => options.LockRetryDelayMilliseconds > 0, "Idempotency lock retry delay must be positive.")
            .ValidateOnStart();
        services.AddOptions<DistributedRateLimitOptions>()
            .Bind(configuration.GetSection(DistributedRateLimitOptions.SectionName))
            .Validate(options => options.Limit > 0, "Distributed rate limit must be positive.")
            .Validate(options => options.WindowSeconds > 0, "Distributed rate limit window must be positive.")
            .ValidateOnStart();
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(options => options.BatchSize is > 0 and <= 100, "Outbox batch size must be between 1 and 100.")
            .Validate(options => options.PollIntervalMilliseconds >= 100, "Outbox poll interval must be at least 100 milliseconds.")
            .Validate(options => options.MaximumRetryDelaySeconds > 0, "Outbox maximum retry delay must be positive.")
            .ValidateOnStart();

        services.AddDbContext<OrdersDbContext>((serviceProvider, options) =>
            options.UseNpgsql(connectionString)
                .AddNPlusOneDetection(serviceProvider.GetRequiredService<ILoggerFactory>()));
        services.AddOrdersSchemaRegistry(configuration);

        services.AddSingleton<IProducer<string, byte[]>>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                ClientId = $"{options.ClientId}-{instanceId}",
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 10_000,
                SocketTimeoutMs = 10_000
            };

            return new ProducerBuilder<string, byte[]>(config).Build();
        });
        // Milestone 69: a second producer, keyed string/string - OrderCreated is Avro (byte[]), settlement commands are plain JSON, and IProducer can't carry both.
        services.AddSingleton<IProducer<string, string>>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                ClientId = $"{options.ClientId}-commands-{instanceId}",
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 10_000,
                SocketTimeoutMs = 10_000
            };

            return new ProducerBuilder<string, string>(config).Build();
        });
        services.AddSingleton<IAdminClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var config = new AdminClientConfig { BootstrapServers = options.BootstrapServers };
            return new AdminClientBuilder(config).Build();
        });

        services.AddOrdersResilience();
        services.AddOrdersRedis(configuration);

        services.AddScoped<IOrderRepository, Persistence.EfOrderRepository>();
        services.AddScoped<ICouponRepository, Persistence.EfCouponRepository>();
        services.AddScoped<ICustomerRepository, Persistence.EfCustomerRepository>();
        services.AddScoped<IOrderStatusRepository, Persistence.EfOrderStatusRepository>();
        services.AddScoped<IOrderReturnRepository, Persistence.EfOrderReturnRepository>();
        services.Configure<Messaging.PaymentSettlementCommandOptions>(
            configuration.GetSection(Messaging.PaymentSettlementCommandOptions.SectionName));
        services.AddSingleton<Messaging.IPaymentSettlementCommandPublisher, Messaging.KafkaPaymentSettlementCommandPublisher>();
        services.AddScoped<IOrderSummaryRepository, Persistence.EfOrderSummaryRepository>();
        services.AddScoped<IOrderEventStoreRepository, Persistence.EfOrderEventStoreRepository>();
        services.AddSingleton<IOrderCache, RedisOrderCache>();
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddSingleton<RedisSlidingWindowRateLimiter>();
        services.AddSingleton<IOrderEventPublisher, KafkaOrderEventPublisher>();
        services.AddScoped<IOutboxEventDispatcher, OrderOutboxEventDispatcher>();
        services.AddHostedService<OutboxPublisher<OrdersDbContext>>();

        return services;
    }
}
