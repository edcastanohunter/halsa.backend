using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// El ABAC por recurso de ARQ §6.2 (SPEC 08): no basta con el permiso, hay que estar asignado al caso. El recurso
/// es el <see cref="CasoMedicoId"/>, así que se evalúa con <c>IAuthorizationService.AuthorizeAsync(user, casoId, …)</c>
/// (lo hace <see cref="IClinicalAuthorization"/>), nunca en el middleware.
/// </summary>
public sealed record AccesoClinicoRequirement : IAuthorizationRequirement;

/// <summary>
/// Por qué <see cref="AccesoClinicoHandler"/> rechazó: el <see cref="Error"/> de la respuesta y el tenant del caso,
/// si el caso es visible, para la fila <c>DENEGADO</c>.
/// </summary>
public sealed class AccesoClinicoDenegado(IAuthorizationHandler handler, Error error, TenantId? tenantDelCaso)
    : AuthorizationFailureReason(handler, error.Description)
{
    public Error Error { get; } = error;

    public TenantId? TenantDelCaso { get; } = tenantDelCaso;
}

/// <summary>
/// Reglas de la SPEC 08:
/// <list type="bullet">
///   <item><see cref="ActorTipo.Arl"/>: el caso es de su tenant y <c>(sub, null)</c> está asignado.</item>
///   <item><see cref="ActorTipo.Ips"/>: el caso le está remitido y <c>(sub, ips_id)</c> está asignado.</item>
///   <item>Cualquier otro actor: nunca.</item>
/// </list>
/// Un caso que no existe o no es visible falla con <see cref="ClinicoErrors.CasoNoEncontrado"/> (<c>404</c>); uno
/// visible sin asignación, con <see cref="ClinicoErrors.NoAsignado"/> (<c>403</c>).
/// </summary>
internal sealed class AccesoClinicoHandler(ITenantContext tenantContext, ICasoAccessReader reader)
    : AuthorizationHandler<AccesoClinicoRequirement, CasoMedicoId>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AccesoClinicoRequirement requirement,
        CasoMedicoId resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!tenantContext.Resuelto || tenantContext.Tipo is not (ActorTipo.Arl or ActorTipo.Ips))
        {
            context.Fail(new AccesoClinicoDenegado(this, ClinicoErrors.AccesoNoPermitido, tenantDelCaso: null));
            return;
        }

        CasoAcceso? caso = await reader.ObtenerAsync(resource, CancellationToken.None);

        if (caso is null || !Visible(caso))
        {
            context.Fail(new AccesoClinicoDenegado(this, ClinicoErrors.CasoNoEncontrado, tenantDelCaso: null));
            return;
        }

        if (!Asignado(caso))
        {
            context.Fail(new AccesoClinicoDenegado(this, ClinicoErrors.NoAsignado, caso.TenantId));
            return;
        }

        context.Succeed(requirement);
    }

    /// <summary>The RLS already filters, but the rule is checked here too: the reader is not the barrier.</summary>
    private bool Visible(CasoAcceso caso) => tenantContext.Tipo switch
    {
        ActorTipo.Arl => caso.TenantId == tenantContext.TenantId,
        ActorTipo.Ips => tenantContext.IpsId is IpsId ips && caso.RemisionesVigentes.Contains(ips),
        _ => false
    };

    /// <summary>An IPS's assignment only counts with a token of that same IPS; the ARL's, only with the ARL's.</summary>
    private bool Asignado(CasoAcceso caso)
    {
        IpsId? ips = tenantContext.Tipo == ActorTipo.Ips ? tenantContext.IpsId : null;
        return caso.Asignados.Contains((tenantContext.Sub, ips));
    }
}
