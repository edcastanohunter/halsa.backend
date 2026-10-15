using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Clinico;

/// <summary>
/// Un concepto médico, una evolución o un concepto de rehabilitación quedó registrado (SPEC 06, ampliado por la SPEC 10),
/// desde el backoffice o desde la integración de una IPS; también su corrección, que es un registro nuevo. Solo ids y el
/// tipo: ni contenido ni diagnósticos. Lo entrega a la ARL el webhook <c>concepto.registrado</c>.
/// </summary>
/// <param name="Tipo"><c>CONCEPTO_MEDICO</c>, <c>EVOLUCION</c> o <c>CONCEPTO_REHABILITACION</c>.</param>
/// <param name="IpsEmisora">El REPS de la IPS que lo registró, si fue una IPS.</param>
public sealed record RegistroClinicoCreadoIntegrationEvent(
    Guid RegistroId,
    Guid CasoId,
    string TenantId,
    string Tipo,
    string? IpsEmisora) : IntegrationEvent;
