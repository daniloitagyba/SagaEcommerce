using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orders.Worker;

public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    public string TokenUrl { get; init; } = "http://localhost:18081/realms/orders-lab/protocol/openid-connect/token";

    public string ClientId { get; init; } = "orders-worker";

    public string ClientSecret { get; init; } = string.Empty;
}

/// <summary>
/// Orders.Worker's own service identity, closing
/// a real gap - the anti-entropy sweep's two
/// cross-service reads (GET /payments/by-order/{id}, GET /inventory/backorders)
/// were unauthenticated because this service had no credentials to present.
/// Uses a dedicated client carrying only payments:read and inventory:read,
/// rather than the backend-tooling client that can also mutate orders and
/// catalog data.
///
/// Same shape as the KeycloakTokenProvider removed from
/// Storefront.Service: that one existed to mint a token standing in for a
/// *shopper*, which Storefront now forwards instead of minting - a problem
/// Orders.Worker doesn't have, since nothing it calls is on a shopper's
/// behalf. Fetches and caches the token, refreshing shortly before it expires.
/// </summary>
public sealed class KeycloakTokenProvider(
    HttpClient httpClient,
    Microsoft.Extensions.Options.IOptions<KeycloakOptions> options,
    TimeProvider? timeProvider = null) : IDisposable
{
    private readonly KeycloakOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken is not null && _timeProvider.GetUtcNow() < _expiresAt)
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && _timeProvider.GetUtcNow() < _expiresAt)
            {
                return _cachedToken;
            }

            var requestedAt = _timeProvider.GetUtcNow();
            using var response = await httpClient.PostAsync(
                _options.TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Keycloak returned an empty token response.");

            _cachedToken = payload.AccessToken;
            // Refresh 30 seconds early rather than racing the exact expiry instant.
            _expiresAt = requestedAt.AddSeconds(Math.Max(payload.ExpiresIn - 30, 5));
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    // Keycloak's token response is snake_case (access_token, expires_in),
    // not the camelCase JsonSerializerDefaults.Web expects - explicit
    // names here, not a global serializer option, since this is the only
    // place in Orders.Worker that talks to a non-camelCase API.
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
