using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Application.Casos.Queries;

namespace Halsa.Backend.Application.Casos.Queries.GetCasoById;

/// <summary>
/// Retorna el detalle completo de un caso (remisiones + asignaciones incluidas). Se audita: contiene
/// datos personales (Ley 1581/2012) y es la puerta a todo lo demás del caso. El listado no.
/// </summary>
public sealed record GetCasoByIdQuery(CasoMedicoId CasoId) : IQuery<CasoDetalleResponse>, IAuditedQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Caso, CasoId.ToString(), CasoId.Value);
}
