using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Webhooks;

public readonly record struct EntregaWebhookId(Guid Value)
{
    public static EntregaWebhookId New() => new(Guid.CreateVersion7());
}

/// <summary>Se guarda como <c>PENDIENTE</c>, <c>ENTREGADA</c>, <c>DEAD_LETTER</c> o <c>DESCARTADA</c>.</summary>
public enum EstadoEntrega
{
    Pendiente,
    Entregada,

    /// <summary>Más de 24 h fallando. Solo sale de aquí con un reintento por API.</summary>
    DeadLetter,

    /// <summary>Un <c>410 Gone</c> o la baja de la suscripción. También se puede reintentar.</summary>
    Descartada
}

/// <summary>
/// Una entrega de un evento a una suscripción (SPEC 10), en <c>integracion.entregas_webhook</c>: única por
/// <c>(suscripcion, evento)</c>. La crea el fan-out y la envía el dispatcher, los dos como <c>halsa_worker</c> en SQL;
/// la API solo la consulta, la reintenta y la descarta con la baja de su suscripción.
/// </summary>
public sealed class EntregaWebhook
{
    public const int MaximoError = 500;

    // Parameterless constructor required by EF Core materializer.
    private EntregaWebhook() { }

    public EntregaWebhookId Id { get; private set; }

    public SuscripcionWebhookId SuscripcionId { get; private set; }

    /// <summary>El id del mensaje del outbox: estable entre reintentos, el receptor deduplica por él.</summary>
    public Guid EventoId { get; private set; }

    public string Tipo { get; private set; } = string.Empty;

    /// <summary>El cuerpo exacto que se envía y se firma.</summary>
    public string Payload { get; private set; } = string.Empty;

    public EstadoEntrega Estado { get; private set; }

    public int Intentos { get; private set; }

    public DateTimeOffset PrimerIntentoEn { get; private set; }

    public DateTimeOffset ProximoIntentoEn { get; private set; }

    public int? UltimoStatus { get; private set; }

    public string? UltimoError { get; private set; }

    public DateTimeOffset? EntregadaEn { get; private set; }

    /// <summary>
    /// Solo desde <see cref="EstadoEntrega.DeadLetter"/> o <see cref="EstadoEntrega.Descartada"/>: vuelve a
    /// <see cref="EstadoEntrega.Pendiente"/> como nueva, con su ventana de 24 h desde ahora.
    /// </summary>
    public Result Reintentar(DateTimeOffset ahora)
    {
        if (Estado is not (EstadoEntrega.DeadLetter or EstadoEntrega.Descartada))
        {
            return Result.Failure(WebhookErrors.EntregaNoReintentable);
        }

        Estado = EstadoEntrega.Pendiente;
        Intentos = 0;
        PrimerIntentoEn = ahora;
        ProximoIntentoEn = ahora;

        return Result.Success();
    }

    /// <summary>
    /// El backoff de un fallo: <c>min(2^intentos × 30 s, 4 h)</c> con ±20 % de jitter. <paramref name="azar"/> va de 0 a 1.
    /// </summary>
    public static TimeSpan Espera(int intentos, double azar)
    {
        double baseSegundos = Math.Min(Math.Pow(2, intentos) * 30, TimeSpan.FromHours(4).TotalSeconds);
        double jitter = 1 + (((azar * 2) - 1) * 0.2);

        return TimeSpan.FromSeconds(baseSegundos * jitter);
    }

    /// <summary>La ventana de reintentos: pasado este tiempo desde <see cref="PrimerIntentoEn"/>, el fallo es dead letter.</summary>
    public static readonly TimeSpan VentanaReintentos = TimeSpan.FromHours(24);

    /// <summary>Los fallos seguidos que suspenden una suscripción.</summary>
    public const int FallosParaSuspender = 50;
}
