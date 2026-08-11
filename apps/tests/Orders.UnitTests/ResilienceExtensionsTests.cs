using BuildingBlocks;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace Orders.UnitTests;

public sealed class ResilienceExtensionsTests
{
    [Theory]
    [InlineData(ResilienceExtensions.PostgresPipeline)]
    [InlineData(ResilienceExtensions.KafkaProducerPipeline)]
    [InlineData(ResilienceExtensions.RedisPipeline)]
    public void AddOrdersResilienceRegistersNamedPipeline(string pipelineName)
    {
        var provider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        var pipeline = provider.GetPipeline(pipelineName);

        Assert.NotNull(pipeline);
    }

    [Fact]
    public void IsInfrastructureFaultRecognizesCircuitBreakerAndTimeoutExceptions()
    {
        Assert.True(ResilienceExtensions.IsInfrastructureFault(new BrokenCircuitException()));
        Assert.True(ResilienceExtensions.IsInfrastructureFault(new TimeoutRejectedException()));
        Assert.False(ResilienceExtensions.IsInfrastructureFault(new InvalidOperationException()));
    }

    [Fact]
    public async Task PostgresPipelineDoesNotRetryNonTransientApplicationErrors()
    {
        var provider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
        var pipeline = provider.GetPipeline(ResilienceExtensions.PostgresPipeline);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InvalidOperationException("not transient");
            }));

        Assert.Equal(1, attempts);
    }
}
