using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Auditoria;

/// <summary>Filtro de la consulta del oficial de privacidad. Acción y resultado van como código (<c>LEER</c>).</summary>
public sealed record FiltroAccesosAuditados(
    TenantId Tenant,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    Guid? CasoId,
    string? ActorId,
    string? Accion,
    string? Resultado);

/// <summary>
/// Lectura de <c>auditoria.accesos</c>. La tabla no se expone como <c>IQueryable</c>: su fila vive en
/// Infrastructure y nadie fuera de este lector la necesita. Además del filtro por tenant, la RLS limita
/// la lectura al tenant de la sesión.
/// </summary>
public interface IAccesosAuditadosReader
{
    Task<PagedList<AccesoAuditadoResponse>> ListarAsync(
        FiltroAccesosAuditados filtro,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
