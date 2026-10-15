using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Halsa.Bff.Paciente.Proxy;

/// <summary>
/// El proxy hacia la API (SPEC 12). La allowlist es una sola ruta, fija en código: <c>/paciente/api/{**resto}</c> →
/// <c>{Api}/v1/paciente/{**resto}</c>. Cualquier otra ruta no existe (<c>404</c>). Al reenviar se quitan las cookies y el
/// header anti-CSRF, y el <c>Authorization</c> lo pone el BFF con el access token de la sesión: el que traiga el
/// navegador se descarta. De la respuesta de la API no pasa ninguna cookie.
/// </summary>
internal static class ProxyPaciente
{
    public const string RouteId = "paciente-api";

    public const string ClusterId = "api";

    public static IReverseProxyBuilder AddProxyPaciente(this IServiceCollection services, BffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RouteConfig[] rutas =
        [
            new RouteConfig
            {
                RouteId = RouteId,
                ClusterId = ClusterId,
                AuthorizationPolicy = Bff.PoliticaSesion,
                Match = new RouteMatch { Path = $"{Bff.ApiPrefix}/{{**resto}}" },
                Transforms = [new Dictionary<string, string> { ["PathPattern"] = "/v1/paciente/{**resto}" }]
            }
        ];

        ClusterConfig[] clusters =
        [
            new ClusterConfig
            {
                ClusterId = ClusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["api"] = new() { Address = options.ApiBaseUrl }
                }
            }
        ];

        return services
            .AddReverseProxy()
            .LoadFromMemory(rutas, clusters)
            .AddTransforms(contexto =>
            {
                contexto.AddRequestHeaderRemove("Cookie");
                contexto.AddRequestHeaderRemove(Bff.CsrfHeader);
                contexto.AddRequestHeaderRemove("Authorization");
                contexto.AddResponseHeaderRemove("Set-Cookie");

                contexto.AddRequestTransform(async transformacion =>
                {
                    // The route's policy already required the session; OnValidatePrincipal refreshed the token if needed.
                    string? accessToken = await transformacion.HttpContext.GetTokenAsync("access_token");
                    transformacion.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                });
            });
    }
}
