using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.FunctionalTests.Abstractions;

/// <summary>
/// Reemplaza Keycloak en tests. Emite un principal con claims predeterminados de ARL analista.
///
/// Para escenarios específicos, incluye el header <b>X-Test-Claims</b> con un JSON de claims
/// (diccionario <c>string → string</c>, o <c>string → string[]</c> para claims repetidos como
/// <c>permissions</c>) que sobrescribe completamente los predeterminados.
/// Ejemplo: <c>{ "actor_tipo": "ips", "ips_id": "110011234501", "iss": "http://..." }</c>
///
/// Para simular un request sin autenticación, incluye el header <b>X-Unauthenticated: true</b>.
/// </summary>
internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Sub fijo para el usuario de desarrollo en tests.</summary>
    public static readonly Guid TestUserId = Guid.Parse("0195c0de-0000-7000-8000-000000000001");

    /// <summary>
    /// Emisor del realm personal de desarrollo; debe coincidir con <c>PersonalAuthority</c>
    /// en <c>KeycloakOptions</c> para que <c>TenantContextResolver</c> lo acepte.
    /// </summary>
    public const string DefaultIssuer = "http://localhost:8080/realms/halsa";

    public const string PacientesIssuer = "http://localhost:8080/realms/halsa-pacientes";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Header X-Unauthenticated: simula un request sin credenciales
        if (Request.Headers.TryGetValue("X-Unauthenticated", out _))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        IEnumerable<Claim> claims;

        // Header X-Test-Claims: diccionario JSON que sobrescribe todos los claims
        if (Request.Headers.TryGetValue("X-Test-Claims", out var testClaimsHeader))
        {
            string json = testClaimsHeader.ToString();
            Dictionary<string, JsonElement>? claimsDict =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            // An array value (e.g. "permissions") becomes one claim per element, like Keycloak roles.
            claims = claimsDict?.SelectMany(kv => kv.Value.ValueKind == JsonValueKind.Array
                    ? kv.Value.EnumerateArray().Select(item => new Claim(kv.Key, item.ToString()))
                    : [new Claim(kv.Key, kv.Value.ToString())])
                ?? Enumerable.Empty<Claim>();
        }
        else
        {
            // Claims predeterminados: analista ARL con permisos de casos.
            claims =
            [
                new(CurrentUser.SubjectClaimType, TestUserId.ToString()),
                new(CurrentUser.EmailClaimType, "dev@example.com"),
                new("iss", DefaultIssuer),
                new("actor_tipo", "arl"),
                new("tenant_id", "ARL_A"),
                new(CurrentUser.PermissionClaimType, Permissions.CasosRead),
                new(CurrentUser.PermissionClaimType, Permissions.CasosWrite),
                new(CurrentUser.PermissionClaimType, Permissions.CasosAsignar)
            ];
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
