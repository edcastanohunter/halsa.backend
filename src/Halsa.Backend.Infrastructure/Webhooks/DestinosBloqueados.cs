using System.Net;
using System.Net.Sockets;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// Las direcciones a las que un webhook nunca se conecta (SPEC 10): la red propia, la loopback, las privadas, el CGNAT,
/// el link-local (donde viven los metadatos de las nubes), el multicast, y todas las IPv4 mapeadas en IPv6, que podrían
/// esconder cualquiera de las anteriores. También la dirección no especificada <c>::</c>.
/// </summary>
internal static class DestinosBloqueados
{
    private static readonly IPNetwork[] Redes =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10")
    ];

    public static bool EsBloqueada(IPAddress direccion)
    {
        ArgumentNullException.ThrowIfNull(direccion);

        // Every mapped address, explicitly: IPNetwork.Contains does not match them reliably against ::ffff:0:0/96.
        // A scoped link-local address (fe80::1%3) carries its zone; the network check ignores it.
        return direccion.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || direccion.IsIPv4MappedToIPv6
            || Redes.Any(red => red.Contains(direccion));
    }
}

/// <summary>La conexión de un webhook llegó a una dirección bloqueada: el DNS resolvió a la red propia.</summary>
public sealed class DestinoBloqueadoException(string host, IPAddress direccion)
    : IOException($"El destino {host} resuelve a {direccion}, una dirección bloqueada para webhooks.");
