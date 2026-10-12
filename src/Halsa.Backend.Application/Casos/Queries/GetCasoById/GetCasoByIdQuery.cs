using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Application.Casos.Queries;

namespace Halsa.Backend.Application.Casos.Queries.GetCasoById;

/// <summary>Retorna el detalle completo de un caso (remisiones + asignaciones incluidas).</summary>
public sealed record GetCasoByIdQuery(CasoMedicoId CasoId) : IQuery<CasoDetalleResponse>;
