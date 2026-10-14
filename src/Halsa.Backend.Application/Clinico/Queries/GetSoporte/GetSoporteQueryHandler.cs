using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Queries.GetSoporte;

internal sealed class GetSoporteQueryHandler(
    IClinicalCaseGate gate,
    IReadDbContext readDb,
    IClinicalDbContext db,
    IAuditScope auditScope)
    : IQueryHandler<GetSoporteQuery, SoporteDescargable>
{
    public async Task<Result<SoporteDescargable>> Handle(GetSoporteQuery query, CancellationToken cancellationToken)
    {
        CasoVisible? caso = await gate.CasoVisibleAsync(query.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<SoporteDescargable>(ClinicoErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        // The administrative side (halsa_app) knows which support belongs to the incapacidad and its certificate.
        var incapacidad = await readDb.Incapacidades
            .AsNoTracking()
            .Where(i => i.Id == query.IncapacidadId && i.CasoMedicoId == query.CasoId)
            .Select(i => new { i.SoporteId, i.NumeroCertificado })
            .FirstOrDefaultAsync(cancellationToken);

        if (incapacidad is null)
        {
            return Result.Failure<SoporteDescargable>(SoporteErrors.NoEncontrado);
        }

        // The clinical side (halsa_clinico) has where the file is and the hash of the original.
        Soporte? soporte = await db.Soportes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == incapacidad.SoporteId && s.CasoMedicoId == query.CasoId, cancellationToken);

        return soporte is null
            ? Result.Failure<SoporteDescargable>(SoporteErrors.NoEncontrado)
            : new SoporteDescargable(
                soporte.TenantId, soporte.Id, soporte.KeyId, soporte.Sha256Claro, incapacidad.NumeroCertificado);
    }
}
