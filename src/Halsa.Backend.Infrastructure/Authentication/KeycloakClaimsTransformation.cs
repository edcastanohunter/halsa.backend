using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Keycloak nests roles inside <c>realm_access</c> and <c>resource_access</c> JSON claims.
/// Flattening them here means every downstream policy can use plain role and permission claims.
/// </summary>
internal sealed class KeycloakClaimsTransformation(IOptions<KeycloakOptions> options) : IClaimsTransformation
{
    private readonly KeycloakOptions _options = options.Value;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        foreach (string role in ReadRealmRoles(principal).Concat(ReadClientRoles(principal, _options.ClientId)))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            // Roles double as coarse-grained permissions until you map them explicitly.
            if (!identity.HasClaim(CurrentUser.PermissionClaimType, role))
            {
                identity.AddClaim(new Claim(CurrentUser.PermissionClaimType, role));
            }
        }

        return Task.FromResult(principal);
    }

    private static IEnumerable<string> ReadRealmRoles(ClaimsPrincipal principal)
    {
        string? realmAccess = principal.FindFirst("realm_access")?.Value;

        return string.IsNullOrWhiteSpace(realmAccess) ? [] : ReadRoles(realmAccess, propertyName: null);
    }

    private static IEnumerable<string> ReadClientRoles(ClaimsPrincipal principal, string clientId)
    {
        string? resourceAccess = principal.FindFirst("resource_access")?.Value;

        return string.IsNullOrWhiteSpace(resourceAccess) || string.IsNullOrWhiteSpace(clientId)
            ? []
            : ReadRoles(resourceAccess, clientId);
    }

    private static IEnumerable<string> ReadRoles(string json, string? propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;

        if (propertyName is not null)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement client))
            {
                return [];
            }

            root = client;
        }

        if (!root.TryGetProperty("roles", out JsonElement roles) || roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. roles.EnumerateArray()
            .Select(role => role.GetString())
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role!)];
    }
}
