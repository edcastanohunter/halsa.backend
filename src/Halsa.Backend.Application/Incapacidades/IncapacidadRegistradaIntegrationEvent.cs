using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// Una incapacidad quedó registrada (SPEC 07). Solo datos administrativos y tipos primitivos: es contrato público, y
/// el outbox lo guarda en JSON por su nombre de tipo. El webhook <c>incapacidad.registrada</c> llega en la SPEC 10.
/// </summary>
/// <param name="Tipo"><c>INICIAL</c> o <c>PRORROGA</c>.</param>
/// <param name="Alertas">Los códigos de las alertas calculadas en la carga.</param>
public sealed record IncapacidadRegistradaIntegrationEvent(
    Guid IncapacidadId,
    Guid CasoId,
    string TenantId,
    string Tipo,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int Dias,
    int DiasAcumulados,
    IReadOnlyList<string> Alertas) : IntegrationEvent;
