using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuildingBlocks;

public static class ObservabilityExtensions
{
    public static ILoggingBuilder AddOrdersOpenTelemetryLogging(
        this ILoggingBuilder logging,
        string serviceName,
        string serviceInstanceId,
        string environmentName)
    {
        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(CreateResourceBuilder(serviceName, serviceInstanceId, environmentName));
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.AddOtlpExporter();
        });

        return logging;
    }

    public static IServiceCollection AddOrdersObservability(
        this IServiceCollection services,
        string serviceName,
        string serviceInstanceId,
        string environmentName)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceInstanceId: serviceInstanceId)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>("deployment.environment.name", environmentName)
                ]))
            .WithTracing(tracing => tracing
                .SetSampler(new OrdersSampler())
                .AddSource(OrdersTelemetry.SourceName)
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(OrdersTelemetry.SourceName)
                .AddMeter("Polly")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return services;
    }

    private static ResourceBuilder CreateResourceBuilder(
        string serviceName,
        string serviceInstanceId,
        string environmentName)
    {
        return ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceInstanceId: serviceInstanceId)
            .AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment.name", environmentName)
            ]);
    }
}

public sealed class OrdersSampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        if (samplingParameters.ParentContext.TraceId != default)
        {
            var parentIsSampled = samplingParameters.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded);
            return new SamplingResult(
                parentIsSampled ? SamplingDecision.RecordAndSample : SamplingDecision.Drop);
        }

        var isRootDatabaseOperation = string.Equals(samplingParameters.Name, "postgresql", StringComparison.Ordinal)
            || samplingParameters.Name.StartsWith("CONNECT ", StringComparison.Ordinal);
        return new SamplingResult(
            isRootDatabaseOperation ? SamplingDecision.Drop : SamplingDecision.RecordAndSample);
    }
}
