using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;

namespace Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;

/// <summary>
/// Accesos auditados del tenant del actor, del más reciente al más antiguo. <see cref="Desde"/> y
/// <see cref="Hasta"/> son obligatorios, con un rango máximo de 31 días. La consulta se audita a sí misma.
/// </summary>
public sealed record ListAccesosAuditadosQuery(
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    Guid? CasoId = null,
    string? ActorId = null,
    string? Accion = null,
    string? Resultado = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedList<AccesoAuditadoResponse>>, IAuditedQuery
{
    public const int RangoMaximoDias = 31;

    public const int PageSizeMaximo = 200;

    // Explicit: the public Accion is the filter; this one is what the query itself does.
    AccionAuditoria IAuditedQuery.Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Auditoria, null, CasoId);
}

/// <summary>Una fila de <c>auditoria.accesos</c>: todas las columnas salvo <c>hash_registro</c>.</summary>
public sealed record AccesoAuditadoResponse(
    Guid Id,
    DateTimeOffset OcurridoEn,
    string? TenantId,
    string ActorId,
    string ActorTipo,
    string? ActorTenantId,
    string? ActorIpsId,
    IReadOnlyList<string> ActorRoles,
    string? ClientId,
    string? Ip,
    string? UserAgent,
    string CorrelationId,
    string Accion,
    string Resultado,
    string RecursoTipo,
    string? RecursoId,
    Guid? CasoMedicoId,
    string? Motivo,
    string? Acr);
