using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// <c>/v1/paciente/*</c> (SPEC 12): solo un paciente. El actor <see cref="ActorTipo.Paciente"/> lo resuelve
/// <c>TenantContextResolver</c> únicamente para un token del emisor <c>halsa-pacientes</c>, que solo valida el esquema
/// <c>Paciente</c>: un token del personal autentica, pero aquí responde <c>403</c>. Como las demás políticas, no fija el
/// esquema, para que los tests funcionales usen su esquema de prueba.
/// </summary>
public sealed class ActorPacienteRequirement : IAuthorizationRequirement;

internal sealed class ActorPacienteHandler(ITenantContext tenantContext) : AuthorizationHandler<ActorPacienteRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActorPacienteRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Paciente)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
