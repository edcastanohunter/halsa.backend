using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Integraciones.Registros;

/// <summary>
/// <c>atel.reportes_evento</c>: los datos administrativos del FURAT o FUREP que radicó el caso.
/// <see cref="Datos"/> es el cuerpo recibido sin los campos <c>x-clasificacion: clinico</c>.
/// </summary>
public sealed class ReporteEvento
{
    public required CasoMedicoId CasoMedicoId { get; init; }

    public required TenantId TenantId { get; init; }

    public required TipoReporte Tipo { get; init; }

    public required string NumeroReporteOrigen { get; init; }

    public required string Version { get; init; }

    /// <summary>JSON (<c>jsonb</c>).</summary>
    public required string Datos { get; init; }

    public required DateTimeOffset RecibidoEn { get; init; }
}

/// <summary>
/// <c>integracion.solicitudes</c>: cada radicación recibida, permanente. Su id es el <c>solicitudId</c>
/// de la consulta de estado. <c>halsa_app</c> la lee para responderla.
/// </summary>
public sealed class SolicitudIntegracion
{
    public required Guid Id { get; init; }

    public required TenantId TenantId { get; init; }

    public required TipoReporte Tipo { get; init; }

    public required CasoMedicoId CasoMedicoId { get; init; }

    public required string Radicado { get; init; }

    public required DateTimeOffset RecibidoEn { get; init; }

    public string? ClientId { get; init; }

    public required string CorrelationId { get; init; }
}

/// <summary>
/// <c>integracion.solicitudes_idempotentes</c>: la respuesta de una radicación por
/// <c>(emisor, Idempotency-Key)</c>, vigente 72 h. La escribe y la lee <c>IIdempotencyStore</c> con SQL;
/// se mapea para que la migración cree la tabla.
/// </summary>
public sealed class SolicitudIdempotente
{
    /// <summary><c>tenant:{ARL}</c>, <c>ips:{REPS}</c> (SPEC 06) o <c>sub:{sub}</c> (SPEC 07).</summary>
    public required string Emisor { get; init; }

    public required Guid IdempotencyKey { get; init; }

    public string? TenantId { get; init; }

    public required byte[] CuerpoSha256 { get; init; }

    public required Guid SolicitudId { get; init; }

    /// <summary>JSON (<c>jsonb</c>).</summary>
    public required string Respuesta { get; init; }

    public required DateTimeOffset CreadaEn { get; init; }

    public required DateTimeOffset ExpiraEn { get; init; }
}

/// <summary>
/// <c>integracion.mensajes_entrantes</c>: el cuerpo crudo, cifrado con la DEK <c>INTEGRACION</c> del tenant
/// y AAD <c>"{tenant}|{solicitudId}|mensaje"</c>. Es la evidencia de lo que reportó la ARL: se conserva
/// 15 años y nadie la modifica. <c>halsa_app</c> no tiene acceso.
/// </summary>
public sealed class MensajeEntrante
{
    /// <summary>Años que se conserva el mensaje, como la historia clínica.</summary>
    public const int AniosRetencion = 15;

    public required Guid SolicitudId { get; init; }

    public required TenantId TenantId { get; init; }

    public required TipoReporte Tipo { get; init; }

    public required CampoCifrado Cuerpo { get; init; }

    public required DateTimeOffset RecibidoEn { get; init; }

    public required DateOnly RetenerHasta { get; init; }
}
