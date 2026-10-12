using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Casos.Queries.ListCasos;

internal sealed class ListCasosQueryHandler(IReadDbContext db)
    : IQueryHandler<ListCasosQuery, PagedList<CasoResumen>>
{
    public async Task<Result<PagedList<CasoResumen>>> Handle(
        ListCasosQuery query,
        CancellationToken cancellationToken)
    {
        var q = db.CasosMedicos.AsNoTracking();

        if (query.Estado.HasValue)
        {
            q = q.Where(c => c.Estado == query.Estado.Value);
        }

        int total = await q.CountAsync(cancellationToken);

        List<CasoResumen> items = await q
            .OrderByDescending(c => c.RadicadoEn)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(c => new CasoResumen(
                c.Id.Value,
                c.Radicado,
                c.TenantId.Value,
                c.TipoEvento,
                c.Estado,
                c.FechaEvento,
                c.RadicadoEn,
                c.EsGraveOMortal,
                c.Trabajador.Tipo,
                c.Trabajador.Numero))
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedList<CasoResumen>(items, query.Page, query.PageSize, total));
    }
}
