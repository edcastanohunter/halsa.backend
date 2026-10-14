using System.Text.Json.Serialization;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;

/// <summary>
/// La historia clínica del caso, descifrada y en orden cronológico. Sin los registros <c>DICTAMEN_PCL</c> (SPEC 08):
/// esos solo salen por el endpoint del dictamen.
/// </summary>
public sealed record GetRegistrosClinicosQuery(CasoMedicoId CasoId)
    : IQuery<IReadOnlyList<RegistroClinicoResponse>>, IClinicalQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.RegistroClinico, null, CasoId.Value);
}

/// <summary>
/// Un registro descifrado. <see cref="CorregidoPor"/> apunta al registro que lo corrige, si lo hay;
/// un registro corregido se sigue devolviendo, porque la historia no se reescribe.
/// </summary>
public sealed record RegistroClinicoResponse(
    Guid Id,
    Guid CasoId,
    [property: JsonConverter(typeof(TipoRegistroClinicoJsonConverter))] TipoRegistroClinico Tipo,
    ProfesionalRef Profesional,
    DateTimeOffset AtendidoEn,
    DateTimeOffset RegistradoEn,
    DateOnly RetenerHasta,
    Guid? CorrigeA,
    Guid? CorregidoPor,
    ContenidoClinico Contenido);
