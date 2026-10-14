using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database.Auditoria;

/// <summary>
/// Va por el <see cref="ApplicationDbContext"/> del request (<c>halsa_app</c>): la RLS de lectura y de cierre es por
/// <c>app.tenant_id</c>, y el filtro explícito por tenant también deja fuera las revisiones propias pendientes que la
/// política <c>p_rev_select_propias</c> muestra al actor.
/// </summary>
internal sealed class RevisionesEmergenciaStore(ApplicationDbContext db) : IRevisionesEmergenciaStore
{
    public async Task<PagedList<RevisionEmergenciaResponse>> ListarAsync(
        TenantId tenant, bool? pendientes, int page, int pageSize, CancellationToken ct)
    {
        string tenantId = tenant.Value;

        IQueryable<RevisionEmergencia> q = db.RevisionesEmergencia.AsNoTracking().Where(r => r.TenantId == tenantId);

        q = pendientes switch
        {
            true => q.Where(r => r.RevisadoEn == null),
            false => q.Where(r => r.RevisadoEn != null),
            null => q
        };

        int total = await q.CountAsync(ct);

        List<RevisionEmergencia> pagina = await q
            .OrderByDescending(r => r.OcurridoEn)
            .ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedList<RevisionEmergenciaResponse>([.. pagina.Select(Respuesta)], page, pageSize, total);
    }

    public async Task<(ResultadoCierreRevision Resultado, Guid? CasoMedicoId)> CerrarAsync(
        TenantId tenant, Guid id, string revisadoPor, string conclusion, string? observaciones, DateTimeOffset ahora,
        CancellationToken ct)
    {
        string tenantId = tenant.Value;

        RevisionEmergencia? revision = await db.RevisionesEmergencia
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);

        if (revision is null)
        {
            return (ResultadoCierreRevision.NoEncontrada, null);
        }

        if (revision.RevisadoEn is not null)
        {
            return (ResultadoCierreRevision.YaCerrada, revision.CasoMedicoId);
        }

        revision.RevisadoEn = ahora;
        revision.RevisadoPor = revisadoPor;
        revision.Conclusion = conclusion;
        revision.Observaciones = observaciones;

        return (ResultadoCierreRevision.Cerrada, revision.CasoMedicoId);
    }

    private static RevisionEmergenciaResponse Respuesta(RevisionEmergencia r) => new(
        r.Id,
        r.CasoMedicoId,
        r.AccesoId,
        r.ActorId,
        r.Motivo,
        r.OcurridoEn,
        r.VenceRevisionEn,
        r.RevisadoEn is null ? CodigosRevision.Pendiente : CodigosRevision.Cerrada,
        r.RevisadoEn,
        r.RevisadoPor,
        r.Conclusion,
        r.Observaciones);
}
