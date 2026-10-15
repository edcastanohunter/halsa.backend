namespace Halsa.Backend.Application.Webhooks;

/// <summary>La validación de la URL de una suscripción al registrarla o cambiarla (SPEC 10).</summary>
public interface IWebhookUrlPolicy
{
    bool EsValida(string? url);
}
