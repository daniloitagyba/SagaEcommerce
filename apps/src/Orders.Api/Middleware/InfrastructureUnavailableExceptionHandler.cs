using Microsoft.AspNetCore.Diagnostics;
using Orders.Application.Exceptions;

namespace Orders.Api.Middleware;

// A Ports implementation raises InfrastructureUnavailableException whenever
// the database it depends on is unreachable, so every endpoint reports the
// same thing - 503, Retry-After, a stable ProblemDetails shape - instead of
// each endpoint's own try/catch deciding independently (four of the seven
// had one, worded identically; two had none at all and let the raw
// NpgsqlException fall through to a generic, indistinguishable-from-a-
// real-bug 500). Same pattern as BadHttpRequestExceptionHandler, registered
// alongside it.
public sealed class InfrastructureUnavailableExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not InfrastructureUnavailableException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers["Retry-After"] = "5";

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Detail = exception.Message
            }
        });
    }
}
