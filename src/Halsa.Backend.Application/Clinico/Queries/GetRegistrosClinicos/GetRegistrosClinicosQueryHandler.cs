using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;

internal sealed class GetRegistrosClinicosQueryHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    IAuditScope auditScope)
    : IQueryHandler<GetRegistrosClinicosQuery, IReadOnlyList<RegistroClinicoResponse>>
{
    public async Task<Result<IReadOnlyList<RegistroClinicoResponse>>> Handle(
        GetRegistrosClinicosQuery query,
        CancellationToken cancellationToken)
    {
        CasoVisible? caso = await gate.CasoVisibleAsync(query.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<IReadOnlyList<RegistroClinicoResponse>>(ClinicoErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        List<HistoriaClinicaDetalle> registros = await db.Registros
            .AsNoTracking()
            .Where(r => r.CasoMedicoId == query.CasoId)
            .DeLaHistoriaGeneral()
            .OrderBy(r => r.RegistradoEn)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        // If a record was corrected more than once, it points to the latest correction.
        Dictionary<HistoriaClinicaDetalleId, Guid> corregidoPor = registros
            .Where(r => r.CorrigeA is not null)
            .GroupBy(r => r.CorrigeA!.Value)
            .ToDictionary(g => g.Key, g => g.Last().Id.Value);

        var respuesta = new List<RegistroClinicoResponse>(registros.Count);

        foreach (HistoriaClinicaDetalle registro in registros)
        {
            respuesta.Add(await RegistroClinicoLectura.LeerAsync(
                cipher, registro, corregidoPor.TryGetValue(registro.Id, out Guid nuevo) ? nuevo : null, cancellationToken));
        }

        return Result.Success<IReadOnlyList<RegistroClinicoResponse>>(respuesta);
    }
}
