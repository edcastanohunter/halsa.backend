using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// Las incapacidades de un caso (SPEC 07), ordenadas por <c>fecha_inicio</c>, con las alertas calculadas: solo datos
/// administrativos, sin diagnósticos ni profesional. Se audita como <c>LEER / INCAPACIDAD</c>.
/// </summary>
public sealed record ListIncapacidadesQuery(CasoMedicoId CasoId) : IQuery<IReadOnlyList<IncapacidadResponse>>, IAuditedQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Incapacidad, null, CasoId.Value);
}

public static class IncapacidadConsultaErrors
{
    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Incapacidades.ActorNoAutorizado",
            "Solo una persona de una ARL o de una IPS (por remisión) puede consultar las incapacidades de un caso.");
}
