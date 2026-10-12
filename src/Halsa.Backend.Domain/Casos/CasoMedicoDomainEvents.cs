using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Se emite cuando se radica un nuevo caso médico.
/// En esta spec no tiene handlers; las reacciones (plazos, notificaciones) llegan en SPEC 05+.
/// </summary>
public sealed record CasoRadicadoDomainEvent(
    CasoMedicoId CasoId,
    string TenantId,
    string Radicado,
    TipoEvento TipoEvento,
    DateTimeOffset FechaEvento,
    DateTimeOffset RadicadoEn) : DomainEvent;
