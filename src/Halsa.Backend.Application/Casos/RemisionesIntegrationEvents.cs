using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Casos;

// Contratos del outbox de las remisiones (SPEC 02, ampliados por la SPEC 10). Solo tipos primitivos: son contrato
// público, y el outbox los guarda en JSON por su nombre de tipo. Los entrega a la IPS el webhook de la SPEC 10.

/// <summary>
/// La ARL remitió el caso a una IPS. Lleva la identificación del trabajador, un dato personal y no clínico, porque la IPS
/// la necesita para atender. Nada clínico.
/// </summary>
/// <param name="TipoEvento"><c>AccidenteTrabajo</c> o <c>EnfermedadLaboral</c>, como en <c>CasoResponse</c>.</param>
public sealed record CasoRemitidoIntegrationEvent(
    Guid CasoId,
    string TenantId,
    string Radicado,
    Guid RemisionId,
    string IpsId,
    string TipoEvento,
    DateTimeOffset FechaEvento,
    string TrabajadorTipo,
    string TrabajadorNumero) : IntegrationEvent;

/// <summary>La ARL revocó la remisión del caso a una IPS.</summary>
public sealed record RemisionRevocadaIntegrationEvent(
    Guid CasoId,
    string TenantId,
    string Radicado,
    Guid RemisionId,
    string IpsId) : IntegrationEvent;
