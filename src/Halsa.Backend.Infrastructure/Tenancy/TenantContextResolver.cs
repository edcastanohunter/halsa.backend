using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Convierte un <see cref="ClaimsPrincipal"/> ya validado en un <see cref="TenantContextData"/>
/// o en un error 403 con el código que corresponde.
///
/// Reglas según SPEC 01:
/// <list type="table">
///   <item><term>Emisor = realm <c>halsa-pacientes</c></term><description>→ <see cref="ActorTipo.Paciente"/> sin tenant ni IPS.</description></item>
///   <item><term>Emisor = realm <c>halsa</c>, <c>actor_tipo=arl</c></term><description>→ <see cref="ActorTipo.Arl"/>, requiere <c>tenant_id</c> válido.</description></item>
///   <item><term>Emisor = realm <c>halsa</c>, <c>actor_tipo=ips</c></term><description>→ <see cref="ActorTipo.Ips"/>, requiere <c>ips_id</c> válido.</description></item>
///   <item><term>Emisor = realm <c>halsa</c>, <c>actor_tipo=integracion</c></term><description>→ <see cref="ActorTipo.Integracion"/>, requiere exactamente uno de <c>tenant_id</c> o <c>ips_id</c>.</description></item>
///   <item><term>Emisor = realm <c>halsa</c>, <c>actor_tipo</c> ausente / reservado / desconocido</term><description>→ <see cref="TenantErrors.ActorTipoInvalido"/>.</description></item>
/// </list>
/// </summary>
public sealed class TenantContextResolver(
    IOptions<KeycloakOptions> options,
    ILogger<TenantContextResolver> logger)
{
    private readonly KeycloakOptions _options = options.Value;

    public Result<TenantContextData> Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? sub = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(sub))
        {
            logger.LogWarning("Token sin claim 'sub'.");
            return Result.Failure<TenantContextData>(TenantErrors.ClaimsIncompletos);
        }

        string? iss = principal.FindFirstValue("iss");

        // ── Realm de pacientes: el tipo se determina solo por el emisor ───────
        if (iss == _options.PacientesAuthority)
        {
            return Result.Success(new TenantContextData(ActorTipo.Paciente, sub, null, null));
        }

        // ── Realm de personal ─────────────────────────────────────────────────
        string? actorTipoRaw = principal.FindFirstValue("actor_tipo");

        return actorTipoRaw switch
        {
            "arl" => ResolveArl(sub, principal),
            "ips" => ResolveIps(sub, principal),
            "integracion" => ResolveIntegracion(sub, principal),

            // paciente y sistema son valores reservados que NO pueden venir del realm halsa
            "paciente" or "sistema" or null => FailActorTipoInvalido(actorTipoRaw),

            _ => FailActorTipoInvalido(actorTipoRaw)
        };
    }

    private Result<TenantContextData> ResolveArl(string sub, ClaimsPrincipal principal)
    {
        string? tenantIdRaw = principal.FindFirstValue("tenant_id");

        if (!TenantId.TryCreate(tenantIdRaw, out TenantId tenantId))
        {
            logger.LogWarning(
                "actor_tipo=arl pero tenant_id inválido o ausente (valor: {TenantId}).", tenantIdRaw);
            return Result.Failure<TenantContextData>(TenantErrors.ClaimsIncompletos);
        }

        return Result.Success(new TenantContextData(ActorTipo.Arl, sub, tenantId, null));
    }

    private Result<TenantContextData> ResolveIps(string sub, ClaimsPrincipal principal)
    {
        string? ipsIdRaw = principal.FindFirstValue("ips_id");

        if (!IpsId.TryCreate(ipsIdRaw, out IpsId ipsId))
        {
            logger.LogWarning(
                "actor_tipo=ips pero ips_id inválido o ausente (valor: {IpsId}).", ipsIdRaw);
            return Result.Failure<TenantContextData>(TenantErrors.ClaimsIncompletos);
        }

        return Result.Success(new TenantContextData(ActorTipo.Ips, sub, null, ipsId));
    }

    private Result<TenantContextData> ResolveIntegracion(string sub, ClaimsPrincipal principal)
    {
        string? tenantIdRaw = principal.FindFirstValue("tenant_id");
        string? ipsIdRaw = principal.FindFirstValue("ips_id");

        bool hasTenantId = TenantId.TryCreate(tenantIdRaw, out TenantId tenantId);
        bool hasIpsId = IpsId.TryCreate(ipsIdRaw, out IpsId ipsId);

        // Debe venir exactamente uno de los dos
        if (hasTenantId == hasIpsId) // ambos o ninguno
        {
            logger.LogWarning(
                "actor_tipo=integracion debe tener exactamente uno de tenant_id ({TenantId}) " +
                "o ips_id ({IpsId}).", tenantIdRaw, ipsIdRaw);
            return Result.Failure<TenantContextData>(TenantErrors.ClaimsIncompletos);
        }

        TenantId? resolvedTenantId = hasTenantId ? tenantId : null;
        IpsId? resolvedIpsId = hasIpsId ? ipsId : null;

        return Result.Success(
            new TenantContextData(ActorTipo.Integracion, sub, resolvedTenantId, resolvedIpsId));
    }

    private Result<TenantContextData> FailActorTipoInvalido(string? actorTipo)
    {
        logger.LogWarning("actor_tipo inválido o no permitido en este realm: {ActorTipo}.", actorTipo);
        return Result.Failure<TenantContextData>(TenantErrors.ActorTipoInvalido);
    }
}
