using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Webhooks;

public readonly record struct SuscripcionWebhookId(Guid Value)
{
    public static SuscripcionWebhookId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Se guarda como <c>ACTIVA</c>, <c>PAUSADA</c>, <c>SUSPENDIDA</c> o <c>ELIMINADA</c>.</summary>
public enum EstadoSuscripcion
{
    Activa,
    Pausada,

    /// <summary>La puso el dispatcher: 50 fallos seguidos o un <c>410 Gone</c>. Solo el emisor la reactiva.</summary>
    Suspendida,

    /// <summary>Final: la baja por API. Nunca se borra la fila.</summary>
    Eliminada
}

/// <summary>
/// Una suscripción a webhooks salientes (SPEC 10), en <c>integracion.suscripciones_webhook</c>. La crea la service
/// account de una ARL (<c>tenant:{ARL}</c>) o de una IPS (<c>ips:{REPS}</c>), y solo la ve su emisor (RLS).
///
/// El secreto se guarda envuelto con la KEK (<see cref="SecretoCt"/>, nunca en claro). Durante las 24 h siguientes a una
/// rotación, el anterior sigue en <see cref="SecretoAnteriorCt"/> y el dispatcher firma con los dos.
/// </summary>
public sealed class SuscripcionWebhook
{
    public const int MaximoUrl = 2048;

    /// <summary>Cuánto sigue firmando el secreto anterior después de una rotación.</summary>
    public static readonly TimeSpan TransicionRotacion = TimeSpan.FromHours(24);

    // Parameterless constructor required by EF Core materializer.
    private SuscripcionWebhook() { }

    public SuscripcionWebhookId Id { get; private set; }

    /// <summary><c>tenant:{ARL}</c> o <c>ips:{REPS}</c>, como en la idempotencia.</summary>
    public string Emisor { get; private set; } = string.Empty;

    public TenantId? TenantId { get; private set; }

    public IpsId? IpsId { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public string[] Eventos { get; private set; } = [];

    public EstadoSuscripcion Estado { get; private set; }

    public byte[] SecretoCt { get; private set; } = [];

    /// <summary>El id de la KEK que envolvió el secreto.</summary>
    public string SecretoKeyId { get; private set; } = string.Empty;

    public byte[]? SecretoAnteriorCt { get; private set; }

    public DateTimeOffset? SecretoAnteriorExpiraEn { get; private set; }

    public int FallosConsecutivos { get; private set; }

    public DateTimeOffset CreadaEn { get; private set; }

    /// <summary>El <c>sub</c> de la service account que registró la URL.</summary>
    public string CreadaPor { get; private set; } = string.Empty;

    public DestinatarioWebhook Destinatario => IpsId is null ? DestinatarioWebhook.Arl : DestinatarioWebhook.Ips;

    /// <summary>El emisor de una ARL.</summary>
    public static string EmisorDe(TenantId tenant) => $"tenant:{tenant.Value}";

    /// <summary>El emisor de una IPS.</summary>
    public static string EmisorDe(IpsId ips) => $"ips:{ips.Value}";

    /// <summary>
    /// Una suscripción <see cref="EstadoSuscripcion.Activa"/>. La URL y los eventos ya están validados; el secreto ya
    /// está envuelto, con el <paramref name="id"/> en su AAD, así que el id se genera antes.
    /// </summary>
    public static Result<SuscripcionWebhook> Crear(
        SuscripcionWebhookId id,
        TenantId? tenantId,
        IpsId? ipsId,
        string url,
        IReadOnlyCollection<string> eventos,
        byte[] secretoCt,
        string secretoKeyId,
        string creadaPor,
        DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        ArgumentNullException.ThrowIfNull(secretoCt);

        if ((tenantId is null) == (ipsId is null))
        {
            throw new ArgumentException("Una suscripción es de una ARL o de una IPS, nunca de las dos.");
        }

        var suscripcion = new SuscripcionWebhook
        {
            Id = id,
            Emisor = tenantId is { } tenant ? EmisorDe(tenant) : EmisorDe(ipsId!.Value),
            TenantId = tenantId,
            IpsId = ipsId,
            Url = url,
            Estado = EstadoSuscripcion.Activa,
            SecretoCt = secretoCt,
            SecretoKeyId = secretoKeyId,
            CreadaEn = ahora,
            CreadaPor = creadaPor
        };

        Result eventosValidos = suscripcion.AsignarEventos(eventos);

        return eventosValidos.IsFailure
            ? Result.Failure<SuscripcionWebhook>(eventosValidos.Error)
            : Result.Success(suscripcion);
    }

    /// <summary>
    /// <c>PATCH</c>: cambia lo que llegue. El emisor solo pone <see cref="EstadoSuscripcion.Activa"/> o
    /// <see cref="EstadoSuscripcion.Pausada"/>; reactivar una suspendida reinicia <see cref="FallosConsecutivos"/>.
    /// </summary>
    public Result Cambiar(string? url, IReadOnlyCollection<string>? eventos, EstadoSuscripcion? estado)
    {
        if (Estado == EstadoSuscripcion.Eliminada)
        {
            return Result.Failure(WebhookErrors.SuscripcionNoEncontrada);
        }

        if (estado is not (null or EstadoSuscripcion.Activa or EstadoSuscripcion.Pausada))
        {
            return Result.Failure(WebhookErrors.EstadoNoPermitido);
        }

        if (eventos is not null)
        {
            Result asignados = AsignarEventos(eventos);
            if (asignados.IsFailure)
            {
                return asignados;
            }
        }

        if (url is not null)
        {
            Url = url;
        }

        if (estado is { } nuevo)
        {
            if (nuevo == EstadoSuscripcion.Activa && Estado == EstadoSuscripcion.Suspendida)
            {
                FallosConsecutivos = 0;
            }

            Estado = nuevo;
        }

        return Result.Success();
    }

    /// <summary><c>DELETE</c>: pasa a <see cref="EstadoSuscripcion.Eliminada"/>. Sus entregas pendientes las descarta quien llama.</summary>
    public Result Eliminar()
    {
        if (Estado == EstadoSuscripcion.Eliminada)
        {
            return Result.Failure(WebhookErrors.SuscripcionNoEncontrada);
        }

        Estado = EstadoSuscripcion.Eliminada;
        return Result.Success();
    }

    /// <summary>
    /// El secreto nuevo firma desde ya; el actual pasa a anterior y firma <see cref="TransicionRotacion"/> más. Los dos
    /// llegan envueltos con la misma KEK (<paramref name="keyId"/>): hay una sola columna de key id.
    /// </summary>
    public Result RotarSecreto(byte[] nuevoCt, byte[] actualCt, string keyId, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(nuevoCt);
        ArgumentNullException.ThrowIfNull(actualCt);

        if (Estado == EstadoSuscripcion.Eliminada)
        {
            return Result.Failure(WebhookErrors.SuscripcionNoEncontrada);
        }

        SecretoAnteriorCt = actualCt;
        SecretoAnteriorExpiraEn = ahora + TransicionRotacion;
        SecretoCt = nuevoCt;
        SecretoKeyId = keyId;

        return Result.Success();
    }

    private Result AsignarEventos(IReadOnlyCollection<string> eventos)
    {
        if (eventos.Count == 0)
        {
            return Result.Failure(WebhookErrors.EventosRequeridos);
        }

        if (eventos.Any(e => !EventosWebhook.Admite(e, Destinatario)))
        {
            return Result.Failure(WebhookErrors.EventoNoPermitido(Destinatario));
        }

        Eventos = [.. eventos.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return Result.Success();
    }
}
