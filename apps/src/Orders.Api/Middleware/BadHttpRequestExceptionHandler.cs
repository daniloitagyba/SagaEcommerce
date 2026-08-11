using Microsoft.AspNetCore.Diagnostics;

namespace Orders.Api.Middleware;

// Minimal APIs wrap a malformed JSON body's JsonException in a
// BadHttpRequestException with StatusCode already set to 400 - but the
// default UseExceptionHandler()+AddProblemDetails() pipeline ignores that
// and always emits a generic 500, which is indistinguishable from an
// actual server fault. This restores the status the framework already
// computed.
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
