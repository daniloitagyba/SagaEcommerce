using BuildingBlocks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orders.UnitTests;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task ValidInboundValueFlowsToScopeContextAndResponse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "checkout-123";
        var nextCalled = false;
        var middleware = new CorrelationIdMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("checkout-123", context.Items[MessagingHeaders.CorrelationId]);
        Assert.Equal("checkout-123", context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task OversizedInboundValueIsRejectedAndReplaced()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = new string('x', 129);
        var middleware = new CorrelationIdMiddleware(
            _ => Task.CompletedTask,
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        var generated = Assert.IsType<string>(context.Items[MessagingHeaders.CorrelationId]);
        Assert.Equal(32, generated.Length);
        Assert.DoesNotContain('x', generated);
        Assert.Equal(generated, context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }
}
