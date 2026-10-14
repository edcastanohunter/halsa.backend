using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistroClinico;

/// <summary>
/// Un registro de la historia clínica del caso, descifrado (SPEC 08). Un registro <c>DICTAMEN_PCL</c> no existe para
/// esta consulta: responde <see cref="ClinicoErrors.RegistroNoEncontrado"/>.
/// </summary>
public sealed record GetRegistroClinicoQuery(CasoMedicoId CasoId, HistoriaClinicaDetalleId RegistroId)
    : IQuery<RegistroClinicoResponse>, IClinicalQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.RegistroClinico, RegistroId.Value.ToString(), CasoId.Value);
}
