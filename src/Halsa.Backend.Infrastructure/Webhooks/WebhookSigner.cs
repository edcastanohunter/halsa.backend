using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// La cabecera <c>Halsa-Signature</c> (SPEC 10):
/// <c>t=&lt;unix&gt;,v1=&lt;hex(HMAC-SHA256(secreto, t + "." + cuerpo))&gt;[,v1=&lt;hex con el secreto anterior&gt;]</c>.
/// Durante una rotación van dos <c>v1</c>: primero el del secreto nuevo. El receptor rechaza un <c>t</c> de más de 5
/// minutos (contrato).
/// </summary>
internal static class WebhookSigner
{
    public const string Header = "Halsa-Signature";

    public static string Firmar(long timestamp, string cuerpo, byte[] secreto, byte[]? secretoAnterior = null)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        ArgumentNullException.ThrowIfNull(secreto);

        string t = timestamp.ToString(CultureInfo.InvariantCulture);
        byte[] firmado = Encoding.UTF8.GetBytes($"{t}.{cuerpo}");

        var cabecera = new StringBuilder($"t={t},v1={Hmac(secreto, firmado)}");
        if (secretoAnterior is not null)
        {
            cabecera.Append(",v1=").Append(Hmac(secretoAnterior, firmado));
        }

        return cabecera.ToString();
    }

    private static string Hmac(byte[] secreto, byte[] firmado) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(secreto, firmado));
}
