using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Halsa.Bff.Paciente.Sesion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Halsa.Bff.Paciente.Endpoints;

/// <summary>
/// Las rutas propias del BFF (SPEC 12), bajo <c>/paciente/bff</c>. Ninguna devuelve un token al navegador.
/// </summary>
internal static class BffEndpoints
{
    /// <summary>La respuesta de <c>GET /paciente/bff/usuario</c>.</summary>
    internal sealed record Usuario(string? Nombre, string? Correo, bool Vinculado);

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder bff = app.MapGroup(Bff.BffPrefix);

        // GET /paciente/bff/login?returnUrl= — starts the code + PKCE flow. Only a local URL comes back after it.
        bff.MapGet("login", (string? returnUrl) =>
        {
            string destino = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
            if (!EsUrlLocal(destino))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad request",
                    detail: "returnUrl debe ser una URL relativa de este sitio.");
            }

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = destino },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        // POST /paciente/bff/logout — X-Halsa-Csrf required (GuardasBff). Ends the Keycloak session through the
        // server channel, then the local one (the Redis entry and the cookie). Idempotent: 204 without a session too.
        bff.MapPost("logout", async (HttpContext context, SesionTokens tokens) =>
        {
            AuthenticateResult sesion = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (sesion.Succeeded)
            {
                await tokens.CerrarEnKeycloakAsync(sesion.Properties?.GetTokenValue("refresh_token"), context.RequestAborted);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // GET /paciente/bff/usuario — who is signed in, and whether the account is linked yet. 401 without a session.
        bff.MapGet("usuario", async (HttpContext context, IHttpClientFactory httpClientFactory) =>
        {
            AuthenticateResult sesion = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!sesion.Succeeded)
            {
                return Results.Unauthorized();
            }

            string? accessToken = sesion.Properties?.GetTokenValue("access_token");

            // A linked account always sees at least the case of the code it redeemed.
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/paciente/mis-casos");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage respuesta = await httpClientFactory.CreateClient(Bff.ClienteApi)
                .SendAsync(request, context.RequestAborted);

            if (!respuesta.IsSuccessStatusCode)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Bad gateway");
            }

            using JsonDocument casos = await JsonDocument.ParseAsync(
                await respuesta.Content.ReadAsStreamAsync(context.RequestAborted), cancellationToken: context.RequestAborted);

            ClaimsPrincipal usuario = sesion.Principal!;
            return Results.Ok(new Usuario(
                usuario.FindFirstValue("name") ?? usuario.FindFirstValue("preferred_username"),
                usuario.FindFirstValue("email"),
                casos.RootElement.ValueKind == JsonValueKind.Array && casos.RootElement.GetArrayLength() > 0));
        });

        return app;
    }

    /// <summary>Como <c>Url.IsLocalUrl</c>: empieza por una sola <c>/</c>, sin <c>//</c> ni <c>/\</c> que lo vuelvan absoluto.</summary>
    internal static bool EsUrlLocal(string url) =>
        url.Length > 0 &&
        url[0] == '/' &&
        (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) &&
        !url.Contains('\\', StringComparison.Ordinal) &&
        !url.Any(char.IsControl);
}
