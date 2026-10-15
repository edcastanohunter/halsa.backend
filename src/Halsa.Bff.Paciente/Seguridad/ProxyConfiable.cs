using Microsoft.AspNetCore.HttpOverrides;

namespace Halsa.Bff.Paciente.Seguridad;

/// <summary>
/// SPEC 13: el BFF vive detrás de Nginx (<c>pacientes.&lt;dominio&gt;</c>). Solo del proxy de confianza
/// (<c>Red:ProxyConfiable</c>, la red <c>borde</c> de Docker) se aceptan <c>X-Forwarded-For</c>, <c>-Proto</c> y
/// <c>-Host</c>, con un salto: así el <c>redirect_uri</c> de OIDC y la cookie <c>__Host-</c> ven <c>https</c> y el host
/// público. Sin configurar, solo loopback (el default del framework).
/// </summary>
internal static class ProxyConfiable
{
    public const string Clave = "Red:ProxyConfiable";

    public static IServiceCollection AddProxyConfiable(this IServiceCollection services, IConfiguration configuration)
    {
        string? valor = configuration[Clave];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                       | ForwardedHeaders.XForwardedProto
                                       | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;

            if (string.IsNullOrWhiteSpace(valor))
            {
                return;
            }

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (string red in valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(red));
            }
        });

        return services;
    }
}
