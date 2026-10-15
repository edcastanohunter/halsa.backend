using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// El único cliente HTTP que llama hacia afuera (SPEC 10). Contra el SSRF, el <c>ConnectCallback</c> resuelve el DNS en
/// <b>cada conexión</b> y no conecta si alguna dirección es de <see cref="DestinosBloqueados"/>: un nombre que pasó la
/// validación al registrarse y luego apunta a la red propia (<em>DNS rebinding</em>) se bloquea aquí. Sin proxy, que
/// conectaría por su cuenta, y sin seguir redirecciones: un <c>3xx</c> es la respuesta, y cuenta como fallo.
/// </summary>
internal sealed class SafeWebhookHttpClient : IDisposable
{
    private readonly HttpClient _client;

    public SafeWebhookHttpClient(IOptions<WebhooksOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        WebhooksOptions valor = options.Value;
        bool permitirLocales = valor.PermitirDestinosLocales;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = valor.Timeout,
            // A pooled connection was checked when it opened; renewing it re-resolves the name now and then.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (contexto, ct) => ConectarAsync(contexto.DnsEndPoint, permitirLocales, ct)
        };

        _client = new HttpClient(handler) { Timeout = valor.Timeout };
    }

    /// <summary>
    /// <c>POST</c> de <paramref name="cuerpo"/> tal cual, como <c>application/json</c>. Devuelve la respuesta sin leer el
    /// cuerpo; lanza <see cref="HttpRequestException"/> (con <see cref="DestinoBloqueadoException"/> dentro si fue el
    /// SSRF) o <see cref="TaskCanceledException"/> por timeout.
    /// </summary>
    public async Task<HttpResponseMessage> PostAsync(
        Uri url,
        string cuerpo,
        IReadOnlyDictionary<string, string> cabeceras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cabeceras);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
        };

        foreach ((string nombre, string valor) in cabeceras)
        {
            request.Headers.TryAddWithoutValidation(nombre, valor);
        }

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public void Dispose() => _client.Dispose();

    internal static async ValueTask<Stream> ConectarAsync(DnsEndPoint destino, bool permitirLocales, CancellationToken ct)
    {
        IPAddress[] direcciones = IPAddress.TryParse(destino.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(destino.Host, ct);

        if (!permitirLocales && direcciones.FirstOrDefault(DestinosBloqueados.EsBloqueada) is { } bloqueada)
        {
            throw new DestinoBloqueadoException(destino.Host, bloqueada);
        }

        // Only to the addresses just checked: never let the socket resolve the name again.
        Exception? ultimo = null;
        foreach (IPAddress direccion in direcciones)
        {
            var socket = new Socket(direccion.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(direccion, destino.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                ultimo = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw ultimo ?? new SocketException((int)SocketError.HostNotFound);
    }
}
