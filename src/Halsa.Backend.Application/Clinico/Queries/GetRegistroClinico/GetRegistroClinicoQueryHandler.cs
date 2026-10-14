using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistroClinico;

internal sealed class GetRegistroClinicoQueryHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    IAuditScope auditScope)
    : IQueryHandler<GetRegistroClinicoQuery, RegistroClinicoResponse>
{
    public async Task<Result<RegistroClinicoResponse>> Handle(
        GetRegistroClinicoQuery query,
        CancellationToken cancellationToken)
    {
        CasoVisible? caso = await gate.CasoVisibleAsync(query.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<RegistroClinicoResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        HistoriaClinicaDetalle? registro = await db.Registros
            .AsNoTracking()
            .Where(r => r.Id == query.RegistroId && r.CasoMedicoId == query.CasoId)
            .DeLaHistoriaGeneral()
            .FirstOrDefaultAsync(cancellationToken);

        if (registro is null)
        {
            return Result.Failure<RegistroClinicoResponse>(ClinicoErrors.RegistroNoEncontrado);
        }

        // As in the list: if it was corrected more than once, the latest correction.
        HistoriaClinicaDetalleId? corregidoPor = await db.Registros
            .AsNoTracking()
            .Where(r => r.CorrigeA == registro.Id)
            .OrderByDescending(r => r.RegistradoEn)
            .ThenByDescending(r => r.Id)
            .Select(r => (HistoriaClinicaDetalleId?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return await RegistroClinicoLectura.LeerAsync(cipher, registro, corregidoPor?.Value, cancellationToken);
    }
}
