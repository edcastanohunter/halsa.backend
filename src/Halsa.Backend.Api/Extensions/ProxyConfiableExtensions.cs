using System.Net;
using Halsa.Backend.Api.Middleware;
using Microsoft.AspNetCore.HttpOverrides;

namespace Halsa.Backend.Api.Extensions;

/// <summary>
/// SPEC 13: Nginx es el único proxy de confianza (<c>Red:ProxyConfiable</c>, la red <c>borde</c> de Docker). Solo de él
/// se aceptan <c>X-Forwarded-For</c>, <c>-Proto</c> y <c>-Host</c> (un salto, <c>ForwardLimit=1</c>), de modo que
/// <c>HttpContext.Connection.RemoteIpAddress</c> ya es la IP del cliente cuando la auditoría la lee. Y solo de él se
/// acepta <see cref="IntegrationCertificateBindingMiddleware.CabeceraCertificado"/>: de cualquier otro origen se borra.
/// </summary>
public static class ProxyConfiableExtensions
{
    public static IServiceCollection AddProxyConfiable(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var proxy = ProxyConfiable.Desde(configuration);
        services.AddSingleton(proxy);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                       | ForwardedHeaders.XForwardedProto
                                       | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;

            // Only the configured networks: the framework's loopback default is replaced, not extended.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (System.Net.IPNetwork red in proxy.Redes)
            {
                options.KnownIPNetworks.Add(red);
            }
        });

        return services;
    }

    /// <summary>Lo primero del pipeline: antes de que nada lea la IP, el esquema o el certificado.</summary>
    public static IApplicationBuilder UseProxyConfiable(this IApplicationBuilder app)
    {
        ProxyConfiable proxy = app.ApplicationServices.GetRequiredService<ProxyConfiable>();

        app.Use((context, next) =>
        {
            if (!proxy.Contiene(context.Connection.RemoteIpAddress))
            {
                context.Request.Headers.Remove(IntegrationCertificateBindingMiddleware.CabeceraCertificado);
            }

            return next(context);
        });

        return app.UseForwardedHeaders();
    }
}

/// <summary>Las redes desde las que llega el proxy de confianza. Sin configurar, solo loopback (el default del framework).</summary>
public sealed class ProxyConfiable
{
    public const string Clave = "Red:ProxyConfiable";

    private static readonly System.Net.IPNetwork[] Loopback =
    [
        System.Net.IPNetwork.Parse("127.0.0.0/8"),
        System.Net.IPNetwork.Parse("::1/128")
    ];

    private ProxyConfiable(IReadOnlyList<System.Net.IPNetwork> redes) => Redes = redes;

    public IReadOnlyList<System.Net.IPNetwork> Redes { get; }

    /// <summary>Lee <see cref="Clave"/>: una o varias redes CIDR separadas por coma, p. ej. <c>172.30.0.0/24</c>.</summary>
    public static ProxyConfiable Desde(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? valor = configuration[Clave];

        if (string.IsNullOrWhiteSpace(valor))
        {
            return new ProxyConfiable(Loopback);
        }

        return new ProxyConfiable([.. valor
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Net.IPNetwork.Parse)]);
    }

    public bool Contiene(IPAddress? direccion)
    {
        if (direccion is null)
        {
            return false;
        }

        if (direccion.IsIPv4MappedToIPv6)
        {
            direccion = direccion.MapToIPv4();
        }

        return Redes.Any(red => red.Contains(direccion));
    }
}
