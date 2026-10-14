using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// El ABAC por recurso de ARQ §6.2 (SPEC 08): no basta con el permiso, hay que estar asignado al caso. El recurso
/// es el <see cref="CasoMedicoId"/>, así que se evalúa con <c>IAuthorizationService.AuthorizeAsync(user, casoId, …)</c>
/// (lo hace <see cref="IClinicalAuthorization"/>), nunca en el middleware.
/// </summary>
/// <param name="AdmiteEmergencia">
/// Si el break-the-glass puede saltar la asignación. Solo en las lecturas (SPEC 08): una escritura siempre exige estar
/// asignado.
/// </param>
public sealed record AccesoClinicoRequirement(bool AdmiteEmergencia) : IAuthorizationRequirement;

/// <summary>
/// El acceso de emergencia del request, si lo hubo (SPEC 08). Lo marca <see cref="AccesoClinicoHandler"/> al dejar
/// pasar a un profesional no asignado; <c>AuditWriter</c> lo lee al auditar la lectura, para guardar el motivo y crear
/// la revisión. Scoped: vive lo que dura el request.
/// </summary>
public sealed class AccesoEmergencia
{
    /// <summary>El header con el que el profesional justifica el acceso.</summary>
    public const string HeaderMotivo = "Halsa-Motivo-Acceso";

    public const int MinimoMotivo = 20;

    public const int MaximoMotivo = 500;

    /// <summary>El motivo, si el acceso de este request es de emergencia.</summary>
    public string? Motivo { get; private set; }

    internal void Activar(string motivo) => Motivo = motivo;

    /// <summary>El motivo recortado, si su largo está entre <see cref="MinimoMotivo"/> y <see cref="MaximoMotivo"/>.</summary>
    internal static string? Valido(string? motivo) =>
        motivo?.Trim() is { Length: >= MinimoMotivo and <= MaximoMotivo } recortado ? recortado : null;
}

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
///   <item>
///     <see cref="ActorTipo.Arl"/> o <see cref="ActorTipo.Ips"/> con <c>clinico:emergencia</c> y un
///     <c>Halsa-Motivo-Acceso</c> de 20 caracteres o más, en una lectura: la misma condición de tenant o remisión,
///     sin la asignación. Queda marcado en <see cref="AccesoEmergencia"/>.
///   </item>
///   <item>Cualquier otro actor: nunca.</item>
/// </list>
/// Un caso que no existe o no es visible falla con <see cref="ClinicoErrors.CasoNoEncontrado"/> (<c>404</c>); uno
/// visible sin asignación, con <see cref="ClinicoErrors.NoAsignado"/> (<c>403</c>).
/// </summary>
internal sealed class AccesoClinicoHandler(
    ITenantContext tenantContext,
    ICasoAccessReader reader,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor,
    AccesoEmergencia emergencia)
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

        if (Asignado(caso))
        {
            context.Succeed(requirement);
            return;
        }

        // Break-the-glass: only the assignment is skipped, never the tenant nor the remission (checked above).
        if (requirement.AdmiteEmergencia && currentUser.HasPermission(Permissions.ClinicoEmergencia))
        {
            string? header = httpContextAccessor.HttpContext?.Request.Headers[AccesoEmergencia.HeaderMotivo].ToString();

            if (AccesoEmergencia.Valido(header) is string motivo)
            {
                emergencia.Activar(motivo);
                context.Succeed(requirement);
                return;
            }

            if (!string.IsNullOrEmpty(header))
            {
                context.Fail(new AccesoClinicoDenegado(this, ClinicoErrors.MotivoEmergenciaInvalido, caso.TenantId));
                return;
            }
        }

        context.Fail(new AccesoClinicoDenegado(this, ClinicoErrors.NoAsignado, caso.TenantId));
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
