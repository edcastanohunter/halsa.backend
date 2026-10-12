using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;

/// <summary>La historia clínica del caso, descifrada y en orden cronológico.</summary>
public sealed record GetRegistrosClinicosQuery(CasoMedicoId CasoId)
    : IQuery<IReadOnlyList<RegistroClinicoResponse>>, IClinicalQuery;

/// <summary>
/// Un registro descifrado. <see cref="CorregidoPor"/> apunta al registro que lo corrige, si lo hay;
/// un registro corregido se sigue devolviendo, porque la historia no se reescribe.
/// </summary>
public sealed record RegistroClinicoResponse(
    Guid Id,
    Guid CasoId,
    TipoRegistroClinico Tipo,
    ProfesionalRef Profesional,
    DateTimeOffset AtendidoEn,
    DateTimeOffset RegistradoEn,
    DateOnly RetenerHasta,
    Guid? CorrigeA,
    Guid? CorregidoPor,
    ContenidoClinico Contenido);
