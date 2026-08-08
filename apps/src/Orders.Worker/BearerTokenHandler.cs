using System.Net.Http.Headers;

namespace Orders.Worker;

/// <summary>
/// Attaches orders-api-clients' own client_credentials
/// token to a request before it leaves this service - the one piece
/// KeycloakTokenProvider by itself doesn't do. Shared by both
/// anti-entropy-payments and anti-entropy-inventory (AntiEntropySweeper's
/// two cross-service reads) so the token is fetched, and cached, once.
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
