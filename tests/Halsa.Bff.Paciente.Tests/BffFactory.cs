using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Yarp.ReverseProxy.Forwarder;

namespace Halsa.Bff.Paciente.Tests;

/// <summary>
/// Boots the BFF in memory with nothing outside it: the API and Keycloak are stub handlers that record what they receive,
/// the session store is the in-memory cache (no Redis in <c>Testing</c>), and the Data Protection keys are ephemeral.
/// <c>POST /pruebas/sesion</c> signs a patient in as the OIDC callback would, without Keycloak.
/// </summary>
public sealed class BffFactory : WebApplicationFactory<Program>
{
    public const string AccessToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJwYWNpZW50ZSJ9.firma-del-access-token";
    public const string RefreshToken = "eyJhbGciOiJIUzI1NiJ9.eyJ0eXAiOiJSZWZyZXNoIn0.firma-del-refresh-token";
    public const string IdToken = "eyJhbGciOiJSUzI1NiJ9.eyJ0eXAiOiJJRCJ9.firma-del-id-token";

    public const string KeycloakToken = "https://keycloak.test/realms/halsa-pacientes/protocol/openid-connect/token";
    public const string KeycloakLogout = "https://keycloak.test/realms/halsa-pacientes/protocol/openid-connect/logout";
    public const string KeycloakAuth = "https://keycloak.test/realms/halsa-pacientes/protocol/openid-connect/auth";

    public Stub Api { get; } = new(HttpStatusCode.OK, """[{"casoId":"01999000-0000-7000-8000-0000000000aa"}]""");

    public Stub Keycloak { get; } = new(HttpStatusCode.OK, $$"""
        {"access_token":"nuevo-access-token","refresh_token":"nuevo-refresh-token","expires_in":300}
        """);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("Bff:ApiBaseUrl", "http://api.test");
        builder.UseSetting("Bff:Authority", "https://keycloak.test/realms/halsa-pacientes");
        builder.UseSetting("Bff:ClientSecret", "secreto-de-prueba");

        builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // The proxy and the two named clients go to the stubs.
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new StubForwarderFactory(Api));
            services.AddHttpClient(Bff.ClienteApi).ConfigurePrimaryHttpMessageHandler(() => Api);
            services.AddHttpClient(Sesion.SesionTokens.ClienteKeycloak).ConfigurePrimaryHttpMessageHandler(() => Keycloak);

            // A static discovery document: the OIDC handler never fetches one.
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://keycloak.test/realms/halsa-pacientes",
                    AuthorizationEndpoint = KeycloakAuth,
                    TokenEndpoint = KeycloakToken,
                    EndSessionEndpoint = KeycloakLogout
                });

            services.AddSingleton<IStartupFilter, SesionDePrueba>();
        });
    }

    /// <summary>A client that keeps no cookies of its own: the tests send the session cookie by hand.</summary>
    public HttpClient Cliente() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
        BaseAddress = new Uri("https://localhost")
    });

    /// <summary>
    /// <c>POST /pruebas/sesion?expiraEnSegundos=&amp;iniciadaHaceHoras=</c>: signs <c>paciente.dev</c> in with the three
    /// tokens, as the OIDC callback leaves the session.
    /// </summary>
    private sealed class SesionDePrueba : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/pruebas/sesion", sesion => sesion.Run(async context =>
            {
                int expira = int.Parse(context.Request.Query["expiraEnSegundos"].FirstOrDefault() ?? "3600", CultureInfo.InvariantCulture);
                int hace = int.Parse(context.Request.Query["iniciadaHaceHoras"].FirstOrDefault() ?? "0", CultureInfo.InvariantCulture);

                var identidad = new ClaimsIdentity(
                    [
                        new Claim("sub", "01999000-0000-7000-8000-000000000d01"),
                        new Claim("name", "Paciente Dev"),
                        new Claim("email", "paciente.dev@halsa.dev")
                    ],
                    "pruebas", "name", "roles");

                var propiedades = new AuthenticationProperties();
                propiedades.StoreTokens(
                [
                    new AuthenticationToken { Name = "access_token", Value = AccessToken },
                    new AuthenticationToken { Name = "refresh_token", Value = RefreshToken },
                    new AuthenticationToken { Name = "id_token", Value = IdToken },
                    new AuthenticationToken
                    {
                        Name = "expires_at",
                        Value = DateTimeOffset.UtcNow.AddSeconds(expira).ToString("O", CultureInfo.InvariantCulture)
                    }
                ]);

                if (hace > 0)
                {
                    propiedades.Items[Bff.InicioSesion] =
                        DateTimeOffset.UtcNow.AddHours(-hace).ToString("O", CultureInfo.InvariantCulture);
                }

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identidad), propiedades);
            }));

            next(app);
        };
    }

    private sealed class StubForwarderFactory(Stub api) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(api, disposeHandler: false);
    }
}

/// <summary>A downstream that answers a fixed body and records every request it receives.</summary>
public sealed class Stub(HttpStatusCode status, string cuerpo) : HttpMessageHandler
{
    public ConcurrentQueue<Recibido> Recibidos { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? contenido = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Recibidos.Enqueue(new Recibido(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.Contains("Cookie"),
            request.Headers.Contains(Bff.CsrfHeader),
            contenido));

        var respuesta = new HttpResponseMessage(status)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
        };
        respuesta.Headers.TryAddWithoutValidation("Set-Cookie", "de-la-api=1; Path=/");
        return respuesta;
    }
}

public sealed record Recibido(HttpMethod Metodo, Uri Uri, string? Authorization, bool ConCookie, bool ConCsrf, string? Contenido);
