using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Clinico.Crypto;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// El secreto de una suscripción (SPEC 10): 32 bytes aleatorios, envueltos directamente con la KEK de la SPEC 03 y la AAD
/// <c>"webhook|{suscripcionId}|{keyId}"</c>. No usa una DEK de tenant: las IPS no tienen tenant. En claro solo existe al
/// crearlo (la respuesta de la API, en base64url) y al firmar.
/// </summary>
internal sealed class WebhookSecretProtector(IKeyEncryptionProvider kek) : IWebhookSecretProtector
{
    public const int SecretoBytes = 32;

    public SecretoNuevo Generar(SuscripcionWebhookId suscripcion)
    {
        byte[] secreto = RandomNumberGenerator.GetBytes(SecretoBytes);

        try
        {
            return new SecretoNuevo(Base64Url.EncodeToString(secreto), Proteger(suscripcion, secreto), kek.KekId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secreto);
        }
    }

    public byte[] Reenvolver(SuscripcionWebhookId suscripcion, byte[] secretoCt, string keyId) =>
        Proteger(suscripcion, Desproteger(suscripcion, secretoCt, keyId));

    /// <summary>El secreto en claro. Lanza una <see cref="CryptographicException"/> si la AAD no coincide.</summary>
    public byte[] Desproteger(SuscripcionWebhookId suscripcion, byte[] secretoCt, string keyId) =>
        kek.Desenvolver(secretoCt, Aad(suscripcion, keyId));

    private byte[] Proteger(SuscripcionWebhookId suscripcion, byte[] secreto) =>
        kek.Envolver(secreto, Aad(suscripcion, kek.KekId));

    internal static byte[] Aad(SuscripcionWebhookId suscripcion, string keyId) =>
        Encoding.UTF8.GetBytes($"webhook|{suscripcion.Value}|{keyId}");
}
