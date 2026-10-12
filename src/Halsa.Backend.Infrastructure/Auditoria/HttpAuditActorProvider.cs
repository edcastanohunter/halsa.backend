using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Http;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Saca el actor del request. Sin <c>HttpContext</c> (un test, un proceso en segundo plano) las columnas
/// de red quedan en <c>null</c>. La IP es la de la conexión hasta que la SPEC 13 configure
/// <c>ForwardedHeaders</c>.
/// </summary>
internal sealed class HttpAuditActorProvider(
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
    : IAuditActorProvider
{
    /// <summary>
    /// <c>actor_tipo</c> (y <c>actor_id</c>, si no hay <c>sub</c>) de un request sin contexto de tenant:
    /// un challenge en una ruta auditable, antes de que haya actor.
    /// </summary>
    public const string ActorAnonimo = "anonimo";

    private const string ClientIdClaimType = "azp";
    private const string AcrClaimType = "acr";

    public ActorAuditoria Actual()
    {
        HttpContext? http = httpContextAccessor.HttpContext;
        ClaimsPrincipal? principal = http?.User;

        bool resuelto = tenantContext.Resuelto;

        return new ActorAuditoria(
            Truncar(resuelto ? tenantContext.Sub : principal?.FindFirstValue(CurrentUser.SubjectClaimType) ?? ActorAnonimo, 64),
            resuelto ? tenantContext.Tipo.ToString().ToLowerInvariant() : ActorAnonimo,
            resuelto ? tenantContext.TenantId?.Value : null,
            resuelto ? tenantContext.IpsId?.Value : null,
            [.. currentUser.Permissions],
            TruncarONulo(principal?.FindFirstValue(ClientIdClaimType), 100),
            http?.Connection.RemoteIpAddress,
            TruncarONulo(http?.Request.Headers.UserAgent.ToString(), 300),
            Truncar(http is null ? string.Empty : CorrelationId.From(http), 64),
            TruncarONulo(principal?.FindFirstValue(AcrClaimType), 10));
    }

    private static string Truncar(string valor, int max) => valor.Length <= max ? valor : valor[..max];

    private static string? TruncarONulo(string? valor, int max) =>
        string.IsNullOrEmpty(valor) ? null : Truncar(valor, max);
}
