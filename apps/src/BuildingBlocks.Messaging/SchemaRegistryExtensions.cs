using Confluent.SchemaRegistry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

public static class SchemaRegistryExtensions
{
    public static IServiceCollection AddOrdersSchemaRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SchemaRegistryOptions>()
            .Bind(configuration.GetSection(SchemaRegistryOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Url), "Schema registry URL is required.")
            .ValidateOnStart();

        services.AddSingleton<ISchemaRegistryClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SchemaRegistryOptions>>().Value;
            var config = new SchemaRegistryConfig { Url = options.Url };
            return new CachedSchemaRegistryClient(config);
        });

        return services;
    }
}
