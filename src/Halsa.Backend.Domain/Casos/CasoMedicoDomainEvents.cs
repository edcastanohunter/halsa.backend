using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Se emite cuando se radica un nuevo caso médico.
/// La ingesta (SPEC 05) lo publica como <c>CasoRadicadoIntegrationEvent</c>; los plazos llegan en la SPEC 09.
/// </summary>
public sealed record CasoRadicadoDomainEvent(
    CasoMedicoId CasoId,
    string TenantId,
    string Radicado,
    TipoEvento TipoEvento,
    DateTimeOffset FechaEvento,
    DateTimeOffset RadicadoEn) : DomainEvent;

/// <summary>La validación asíncrona aceptó el caso (SPEC 05). Llega al outbox como <c>caso.validado</c>.</summary>
public sealed record CasoValidadoDomainEvent(
    CasoMedicoId CasoId,
    string TenantId,
    string Radicado,
    DateTimeOffset ValidadoEn) : DomainEvent;

/// <summary>La validación asíncrona rechazó el caso (SPEC 05). Llega al outbox como <c>caso.rechazado</c>.</summary>
public sealed record CasoRechazadoDomainEvent(
    CasoMedicoId CasoId,
    string TenantId,
    string Radicado,
    MotivoRechazo Motivo,
    CasoMedicoId? DuplicadoDe,
    DateTimeOffset RechazadoEn) : DomainEvent;
