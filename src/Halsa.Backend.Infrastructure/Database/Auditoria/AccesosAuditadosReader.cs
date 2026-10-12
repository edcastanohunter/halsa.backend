using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Auditoria;
using Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database.Auditoria;

/// <summary>
/// Va por el <see cref="ApplicationDbContext"/> del request (<c>halsa_app</c>), así que la RLS de
/// <c>p_aud_select</c> limita la lectura al <c>app.tenant_id</c> de la sesión, además del filtro explícito.
/// </summary>
internal sealed class AccesosAuditadosReader(ApplicationDbContext db) : IAccesosAuditadosReader
{
    public async Task<PagedList<AccesoAuditadoResponse>> ListarAsync(
        FiltroAccesosAuditados filtro,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        string tenant = filtro.Tenant.Value;

        // Npgsql only writes timestamptz parameters with offset 0.
        DateTimeOffset desde = filtro.Desde.ToUniversalTime();
        DateTimeOffset hasta = filtro.Hasta.ToUniversalTime();

        IQueryable<AccesoAuditado> q = db.AccesosAuditados
            .AsNoTracking()
            .Where(a => a.TenantId == tenant && a.OcurridoEn >= desde && a.OcurridoEn < hasta);

        if (filtro.CasoId is { } casoId)
        {
            q = q.Where(a => a.CasoMedicoId == casoId);
        }

        if (filtro.ActorId is not null)
        {
            q = q.Where(a => a.ActorId == filtro.ActorId);
        }

        if (filtro.Accion is not null)
        {
            q = q.Where(a => a.Accion == filtro.Accion);
        }

        if (filtro.Resultado is not null)
        {
            q = q.Where(a => a.Resultado == filtro.Resultado);
        }

        int total = await q.CountAsync(cancellationToken);

        List<AccesoAuditado> pagina = await q
            .OrderByDescending(a => a.OcurridoEn)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        List<AccesoAuditadoResponse> items = [.. pagina.Select(a => new AccesoAuditadoResponse(
            a.Id,
            a.OcurridoEn,
            a.TenantId,
            a.ActorId,
            a.ActorTipo,
            a.ActorTenantId,
            a.ActorIpsId,
            a.ActorRoles,
            a.ClientId,
            a.Ip?.ToString(),
            a.UserAgent,
            a.CorrelationId,
            a.Accion,
            a.Resultado,
            a.RecursoTipo,
            a.RecursoId,
            a.CasoMedicoId,
            a.Motivo,
            a.Acr))];

        return new PagedList<AccesoAuditadoResponse>(items, page, pageSize, total);
    }
}
