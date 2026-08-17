using System.Net.Http.Headers;

namespace Orders.Worker;

/// <summary>
/// Adds an access token to requests.
/// </summary>
public sealed class BearerTokenHandler(KeycloakTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
