using Halsa.Backend.Domain.Webhooks;

namespace Halsa.Backend.Application.Webhooks;

/// <summary>Un secreto recién generado: en claro solo para la respuesta, y envuelto para guardar.</summary>
/// <param name="Secreto">Los 32 bytes en base64url. Se muestran una sola vez.</param>
public sealed record SecretoNuevo(string Secreto, byte[] SecretoCt, string KeyId);

/// <summary>El secreto de una suscripción, envuelto con la KEK (SPEC 10). La KEK nunca llega a la aplicación.</summary>
public interface IWebhookSecretProtector
{
    /// <summary>Un secreto aleatorio para <paramref name="suscripcion"/> (su id va en la AAD).</summary>
    SecretoNuevo Generar(SuscripcionWebhookId suscripcion);

    /// <summary>El mismo secreto, envuelto de nuevo con la KEK actual: para conservar el anterior en una rotación.</summary>
    byte[] Reenvolver(SuscripcionWebhookId suscripcion, byte[] secretoCt, string keyId);
}
