using System.Text.Json;

namespace Storefront.Service;

/// <summary>Reads a claim out of a JWT's payload segment without validating the token; malformed input yields null, never an exception.</summary>
internal static class UnverifiedJwt
{
    public static string? TryGetClaim(string? bearerToken, string claimName)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return null;
        }

        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..]
            : bearerToken;

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadJson = Base64UrlDecode(segments[1]);
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(claimName, out var value) ? value.GetString() : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
