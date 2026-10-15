using System.Net;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Domain.Webhooks;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// La validación de la URL al registrarla (SPEC 10): absoluta, <c>https://</c>, sin credenciales, de
/// <see cref="SuscripcionWebhook.MaximoUrl"/> caracteres como máximo, y sin una IP literal bloqueada ni
/// <c>localhost</c>. No resuelve el DNS: un nombre puede cambiar de IP después, así que la defensa real es el
/// <c>ConnectCallback</c> de <see cref="SafeWebhookHttpClient"/>, en cada conexión.
/// </summary>
internal sealed class WebhookUrlPolicy(IOptions<WebhooksOptions> options) : IWebhookUrlPolicy
{
    private readonly bool _permitirLocales = options.Value.PermitirDestinosLocales;

    public bool EsValida(string? url) => EsValida(url, _permitirLocales);

    internal static bool EsValida(string? url, bool permitirLocales)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > SuscripcionWebhook.MaximoUrl
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool esquemaValido = uri.Scheme == Uri.UriSchemeHttps || (permitirLocales && uri.Scheme == Uri.UriSchemeHttp);
        if (!esquemaValido || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        if (permitirLocales)
        {
            return true;
        }

        if (uri.IsLoopback || string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An IP literal is checked now; a name, at every connection.
        return !IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out IPAddress? ip) || !DestinosBloqueados.EsBloqueada(ip);
    }
}
