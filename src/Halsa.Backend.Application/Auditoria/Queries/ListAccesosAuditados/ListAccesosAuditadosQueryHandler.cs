using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;

internal sealed class ListAccesosAuditadosQueryHandler(
    IAccesosAuditadosReader reader,
    ITenantContext tenantContext,
    IAuditScope auditScope)
    : IQueryHandler<ListAccesosAuditadosQuery, PagedList<AccesoAuditadoResponse>>
{
    public async Task<Result<PagedList<AccesoAuditadoResponse>>> Handle(
        ListAccesosAuditadosQuery query,
        CancellationToken cancellationToken)
    {
        // Only an ARL has a tenant whose accesses it can oversee, even if another actor holds auditoria:read.
        if (!tenantContext.Resuelto || tenantContext.Tipo != ActorTipo.Arl || tenantContext.TenantId is not { } tenant)
        {
            return Result.Failure<PagedList<AccesoAuditadoResponse>>(AuditoriaErrors.ActorNoAutorizado);
        }

        // The resource read is the tenant's own audit trail.
        auditScope.TenantDelRecurso(tenant);

        var filtro = new FiltroAccesosAuditados(
            tenant,
            query.Desde,
            query.Hasta,
            query.CasoId,
            query.ActorId,
            // The validator already rejected unknown values; this only normalizes to the stored code.
            AuditoriaCodigos.Normalizar<AccionAuditoria>(query.Accion),
            AuditoriaCodigos.Normalizar<ResultadoAuditoria>(query.Resultado));

        PagedList<AccesoAuditadoResponse> pagina =
            await reader.ListarAsync(filtro, query.Page, query.PageSize, cancellationToken);

        return Result.Success(pagina);
    }
}
