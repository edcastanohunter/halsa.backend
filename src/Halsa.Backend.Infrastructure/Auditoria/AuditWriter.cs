using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Confirma una fila en un <see cref="ApplicationDbContext"/> de un scope propio (rol <c>halsa_app</c>),
/// así no arrastra ni lo que el request tenga pendiente ni un contexto que ya falló. El actor se toma
/// del scope del request, antes de abrir el nuevo.
///
/// Una lectura exitosa de un caso hecha con break-the-glass (<see cref="AccesoEmergencia"/>, SPEC 08) guarda además
/// el motivo en la fila y, en la misma operación, abre una revisión en <c>auditoria.revisiones_emergencia</c>, salvo
/// que el mismo actor ya tenga una pendiente sobre el mismo caso abierta hace menos de 1 h.
/// </summary>
internal sealed class AuditWriter(
    AccesoAuditadoFactory factory,
    IServiceScopeFactory scopeFactory,
    AccesoEmergencia emergencia,
    ITenantContext tenantContext) : IAuditWriter
{
    public async Task RegistrarAsync(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado,
        CancellationToken cancellationToken,
        string? motivo = null)
    {
        ArgumentNullException.ThrowIfNull(objetivo);

        bool porEmergencia = emergencia.Motivo is not null &&
                             accion == AccionAuditoria.Leer &&
                             resultado == ResultadoAuditoria.Exito &&
                             objetivo.CasoMedicoId is not null &&
                             tenant is not null;

        var acceso = factory.Crear(accion, objetivo, tenant, resultado, porEmergencia ? emergencia.Motivo : motivo);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        if (porEmergencia)
        {
            // The grouping reads the actor's own pending reviews, which the RLS shows by app.sub: the new scope's
            // connection needs the request's context.
            scope.ServiceProvider.GetRequiredService<TenantContextHolder>().Establecer(
                new TenantContextData(tenantContext.Tipo, tenantContext.Sub, tenantContext.TenantId, tenantContext.IpsId));
        }

        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.AccesosAuditados.Add(acceso);

        if (porEmergencia && !await TieneRevisionRecienteAsync(db, acceso, cancellationToken))
        {
            db.RevisionesEmergencia.Add(new RevisionEmergencia
            {
                Id = Guid.CreateVersion7(),
                TenantId = acceso.TenantId!,
                AccesoId = acceso.Id,
                ActorId = acceso.ActorId,
                CasoMedicoId = acceso.CasoMedicoId!.Value,
                Motivo = acceso.Motivo!,
                OcurridoEn = acceso.OcurridoEn,
                VenceRevisionEn = acceso.OcurridoEn + RevisionEmergencia.PlazoRevision
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Task<bool> TieneRevisionRecienteAsync(ApplicationDbContext db, AccesoAuditado acceso, CancellationToken ct)
    {
        DateTimeOffset desde = acceso.OcurridoEn - RevisionEmergencia.VentanaAgrupacion;

        return db.RevisionesEmergencia.AnyAsync(
            r => r.ActorId == acceso.ActorId &&
                 r.CasoMedicoId == acceso.CasoMedicoId &&
                 r.RevisadoEn == null &&
                 r.OcurridoEn > desde,
            ct);
    }
}
