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
/// Provides access tokens for Orders Worker.
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
