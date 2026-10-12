using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Integraciones.Eventos;

// Contratos del outbox de la ingesta (SPEC 05). Solo tipos primitivos: son contrato público, y el outbox los
// guarda en JSON por su nombre de tipo, así que renombrarlos deja huérfanos los mensajes pendientes.

/// <summary>Un FURAT o FUREP quedó radicado. Lo consume la validación asíncrona.</summary>
public sealed record CasoRadicadoIntegrationEvent(
    Guid CasoId,
    string TenantId,
    string Radicado,
    string Tipo,
    string NumeroReporteOrigen,
    Guid SolicitudId) : IntegrationEvent;

/// <summary>La validación asíncrona aceptó el caso. El webhook <c>caso.validado</c> llega en la SPEC 10.</summary>
public sealed record CasoValidadoIntegrationEvent(
    Guid CasoId,
    string TenantId,
    string Radicado,
    string NumeroReporteOrigen) : IntegrationEvent;

/// <summary>
/// La validación asíncrona rechazó el caso. El webhook <c>caso.rechazado</c> llega en la SPEC 10, con
/// <see cref="DuplicadoDe"/> para que la ARL revise un posible falso duplicado.
/// </summary>
/// <param name="Motivo"><c>DUPLICADO</c> o <c>NO_AFILIADO</c>.</param>
public sealed record CasoRechazadoIntegrationEvent(
    Guid CasoId,
    string TenantId,
    string Radicado,
    string NumeroReporteOrigen,
    string Motivo,
    Guid? DuplicadoDe) : IntegrationEvent;
