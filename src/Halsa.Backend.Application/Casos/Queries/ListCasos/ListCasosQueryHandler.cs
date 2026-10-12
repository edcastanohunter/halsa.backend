using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Casos.Queries.ListCasos;

internal sealed class ListCasosQueryHandler(IReadDbContext db)
    : IQueryHandler<ListCasosQuery, PagedList<CasoResumenResponse>>
{
    public async Task<Result<PagedList<CasoResumenResponse>>> Handle(
        ListCasosQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<CasoMedico> q = db.CasosMedicos.AsNoTracking();

        if (query.Estado.HasValue)
        {
            q = q.Where(c => c.Estado == query.Estado.Value);
        }

        int total = await q.CountAsync(cancellationToken);

        List<CasoMedico> pagina = await q
            .OrderByDescending(c => c.RadicadoEn)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        List<CasoResumenResponse> items = pagina.Select(c => c.ToResumen()).ToList();

        return Result.Success(new PagedList<CasoResumenResponse>(items, query.Page, query.PageSize, total));
    }
}
