using System.Globalization;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// Step-up (RFC 9470) de un usuario humano: el token debe traer un <c>acr</c> de al menos <see cref="AcrMinimo"/> y,
/// si <see cref="MaxAntiguedadAuth"/> tiene valor, un <c>auth_time</c> más reciente que esa antigüedad. Lo usan la
/// carga de incapacidades (SPEC 07, <c>acr=2</c>) y el dictamen PCL (SPEC 08, además 15 min).
/// </summary>
public sealed record StepUpRequirement(int AcrMinimo, TimeSpan? MaxAntiguedadAuth) : IAuthorizationRequirement;

/// <summary>
/// El motivo de un rechazo por step-up. <c>AuditingAuthorizationResultHandler</c> lo convierte en un <c>401</c> con
/// <c>WWW-Authenticate: Bearer error="insufficient_user_authentication"</c>, en vez del <c>403</c> de cualquier
/// otro rechazo: el usuario puede volver a autenticarse con más nivel y reintentar.
/// </summary>
public sealed class StepUpRequerido(IAuthorizationHandler handler, int acr, TimeSpan? maxAge)
    : AuthorizationFailureReason(handler, "El recurso exige una autenticación más fuerte o más reciente.")
{
    /// <summary>El código del problema de la respuesta <c>401</c>.</summary>
    public const string Codigo = "Auth.StepUpRequerido";

    /// <summary>El valor de <c>acr_values</c> que el cliente debe pedir.</summary>
    public int Acr { get; } = acr;

    /// <summary>El <c>max_age</c> que el cliente debe pedir, si la política exige una autenticación reciente.</summary>
    public TimeSpan? MaxAge { get; } = maxAge;
}

/// <summary>
/// Evalúa <see cref="StepUpRequirement"/>. Solo aplica a las personas (<see cref="ActorTipo.Arl"/> e
/// <see cref="ActorTipo.Ips"/>): una integración no tiene sesión humana, y su garantía es mTLS más
/// <c>private_key_jwt</c> (SPEC 13). Cualquier otro actor no cumple el requisito.
/// </summary>
internal sealed class StepUpHandler(ITenantContext tenantContext, TimeProvider time)
    : AuthorizationHandler<StepUpRequirement>
{
    public const string AcrClaimType = "acr";

    public const string AuthTimeClaimType = "auth_time";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!tenantContext.Resuelto)
        {
            return Task.CompletedTask;
        }

        switch (tenantContext.Tipo)
        {
            case ActorTipo.Integracion:
                context.Succeed(requirement);
                break;

            case ActorTipo.Arl or ActorTipo.Ips when Cumple(context, requirement):
                context.Succeed(requirement);
                break;

            case ActorTipo.Arl or ActorTipo.Ips:
                context.Fail(new StepUpRequerido(this, requirement.AcrMinimo, requirement.MaxAntiguedadAuth));
                break;
        }

        return Task.CompletedTask;
    }

    private bool Cumple(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        if (Acr(context.User.FindFirst(AcrClaimType)?.Value) < requirement.AcrMinimo)
        {
            return false;
        }

        if (requirement.MaxAntiguedadAuth is not TimeSpan maxima)
        {
            return true;
        }

        // Without auth_time the age is unknown, so it is never recent enough.
        return long.TryParse(context.User.FindFirst(AuthTimeClaimType)?.Value, NumberStyles.None,
                   CultureInfo.InvariantCulture, out long segundos) &&
               time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(segundos) <= maxima;
    }

    /// <summary>Keycloak emite <c>"0"</c>, <c>"1"</c> o <c>"2"</c> según el mapeo ACR→LoA; otro valor cuenta como 0.</summary>
    internal static int Acr(string? valor) =>
        int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out int acr) ? acr : 0;
}
