namespace Halsa.Backend.Infrastructure.Observabilidad;

/// <summary>
/// Los valores de la propiedad <c>Alerta</c> de los eventos que Seq convierte en alertas por correo (SPEC 13, runbook 07).
/// Una señal de Seq filtra por <c>Alerta = '...'</c>: el texto del mensaje puede cambiar, estos nombres no.
/// </summary>
public static class Alertas
{
    /// <summary>Un health check en <c>Degraded</c> o <c>Unhealthy</c> (se publica cada minuto mientras dure).</summary>
    public const string HealthCheck = "HealthCheck";

    /// <summary>Un mensaje del outbox agotó sus intentos (<c>attempts &gt;= MaxAttempts</c>) y ya no se reintenta.</summary>
    public const string OutboxAgotado = "OutboxAgotado";

    /// <summary>Una entrega de webhook quedó en <c>DEAD_LETTER</c>.</summary>
    public const string WebhookDeadLetter = "WebhookDeadLetter";

    /// <summary>Una suscripción de webhook quedó <c>SUSPENDIDA</c>.</summary>
    public const string WebhookSuspendida = "WebhookSuspendida";

    /// <summary>Una CRL de la CA de integraciones vence en menos de 48 h (o ya venció, o no se pudo leer).</summary>
    public const string CrlPorVencer = "CrlPorVencer";

    /// <summary>Un certificado TLS público vence en menos de 14 días (o no se pudo leer).</summary>
    public const string TlsPorVencer = "TlsPorVencer";

    /// <summary>
    /// Un sello de auditoría lleva más de 1 h sin publicarse en el WORM. Pendiente de la SPEC 11 (sellado): todavía
    /// nada lo emite, y la señal de Seq queda creada pero inactiva (runbook 07).
    /// </summary>
    public const string SelloSinPublicar = "SelloSinPublicar";
}
