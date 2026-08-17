using Microsoft.AspNetCore.Diagnostics;

namespace Orders.Api.Middleware;

public sealed class BadHttpRequestExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badHttpRequestException)
        {
            return false;
        }

        httpContext.Response.StatusCode = badHttpRequestException.StatusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = badHttpRequestException.StatusCode }
        });
    }
}
