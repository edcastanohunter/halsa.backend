using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Webhooks;

public static class WebhookErrors
{
    public static readonly Error ActorNoAutorizado = Error.Forbidden(
        "Webhook.ActorNoAutorizado",
        "Solo la service account de integración de una ARL o de una IPS gestiona suscripciones.");

    public static readonly Error SuscripcionNoEncontrada = Error.NotFound(
        "Webhook.SuscripcionNoEncontrada",
        "La suscripción no existe.");

    public static readonly Error EntregaNoEncontrada = Error.NotFound(
        "Webhook.EntregaNoEncontrada",
        "La entrega no existe.");

    public static readonly Error UrlInvalida = Error.Problem(
        "Webhook.UrlInvalida",
        "La URL debe ser https://, sin credenciales, de 2048 caracteres como máximo, y no puede apuntar a una dirección privada.");

    public static readonly Error EventosRequeridos = Error.Problem(
        "Webhook.EventosRequeridos",
        "La suscripción necesita al menos un evento.");

    public static readonly Error EstadoNoPermitido = Error.Problem(
        "Webhook.EstadoNoPermitido",
        "El estado solo puede cambiarse a ACTIVA o PAUSADA.");

    public static readonly Error EntregaNoReintentable = Error.Conflict(
        "Webhook.EntregaNoReintentable",
        "Solo se reintenta una entrega en DEAD_LETTER o DESCARTADA.");

    public static Error EventoNoPermitido(DestinatarioWebhook destinatario) => Error.Problem(
        "Webhook.EventoNoPermitido",
        destinatario == DestinatarioWebhook.Arl
            ? "Una ARL solo se suscribe a los eventos con destinatario ARL."
            : "Una IPS solo se suscribe a caso.remitido y remision.revocada.");
}
