using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Casos.Queries.GetCasoById;

internal sealed class GetCasoByIdQueryHandler(IReadDbContext db, IAuditScope auditScope)
    : IQueryHandler<GetCasoByIdQuery, CasoDetalleResponse>
{
    public async Task<Result<CasoDetalleResponse>> Handle(
        GetCasoByIdQuery query,
        CancellationToken cancellationToken)
    {
        CasoMedico? caso = await db.CasosMedicos
            .AsNoTracking()
            .Include(c => c.Remisiones)
            .Include(c => c.Asignaciones)
            .FirstOrDefaultAsync(c => c.Id == query.CasoId, cancellationToken);

        // Invisible y ausente responden igual: 404, nunca 403.
        if (caso is null)
        {
            return Result.Failure<CasoDetalleResponse>(CasoMedicoErrors.NoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        var detalle = new CasoDetalleResponse(
            caso.ToResumen(),
            CasoResponseMapping.Documento(caso.Empleador),
            caso.MunicipioDivipola,
            caso.EsGraveOMortal,
            caso.Remisiones
                .Where(r => r.Vigente)
                .Select(r => new RemisionResponse(r.Id.Value, r.IpsId.Value, r.RemitidoEn))
                .ToList(),
            caso.Asignaciones.Select(a => a.Sub).ToList());

        return Result.Success(detalle);
    }
}
