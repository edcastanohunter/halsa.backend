using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Casos.Queries.GetCasoById;

internal sealed class GetCasoByIdQueryHandler(IReadDbContext db)
    : IQueryHandler<GetCasoByIdQuery, CasoDetalle>
{
    public async Task<Result<CasoDetalle>> Handle(
        GetCasoByIdQuery query,
        CancellationToken cancellationToken)
    {
        CasoMedico? caso = await db.CasosMedicos
            .AsNoTracking()
            .Include(c => c.Remisiones)
            .Include(c => c.Asignaciones)
            .FirstOrDefaultAsync(c => c.Id == query.CasoId, cancellationToken);

        if (caso is null)
        {
            return Result.Failure<CasoDetalle>(CasoMedicoErrors.NoEncontrado);
        }

        var detalle = new CasoDetalle(
            caso.Id.Value,
            caso.Radicado,
            caso.TenantId.Value,
            caso.TipoEvento,
            caso.Estado,
            caso.FechaEvento,
            caso.RadicadoEn,
            caso.EsGraveOMortal,
            caso.MunicipioDivipola,
            new IdentificacionDto(caso.Trabajador.Tipo, caso.Trabajador.Numero),
            new IdentificacionDto(caso.Empleador.Tipo, caso.Empleador.Numero),
            caso.Calificacion is null ? null : new CalificacionDto(
                caso.Calificacion.PorcentajePcl,
                caso.Calificacion.FechaEstructuracion,
                caso.Calificacion.Origen),
            caso.Remisiones.Select(r => new RemisionDto(
                r.Id.Value,
                r.IpsId.Value,
                r.RemitidoPor,
                r.RemitidoEn,
                r.Vigente,
                r.RevocadaEn,
                r.RevocadaPor)).ToList(),
            caso.Asignaciones.Select(a => new AsignacionDto(
                a.Sub,
                a.AsignadoPor,
                a.AsignadoEn)).ToList());

        return Result.Success(detalle);
    }
}
