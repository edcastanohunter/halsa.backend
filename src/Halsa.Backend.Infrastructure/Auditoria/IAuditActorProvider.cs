using System.Net;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>Quién accede y desde dónde, en el momento del acceso.</summary>
internal sealed record ActorAuditoria(
    string ActorId,
    string ActorTipo,
    string? ActorTenantId,
    string? ActorIpsId,
    string[] Roles,
    string? ClientId,
    IPAddress? Ip,
    string? UserAgent,
    string CorrelationId,
    string? Acr);

/// <summary>Los datos del actor de una fila de auditoría (<c>ITenantContext</c>, <c>ICurrentUser</c> y <c>HttpContext</c>).</summary>
internal interface IAuditActorProvider
{
    ActorAuditoria Actual();
}
