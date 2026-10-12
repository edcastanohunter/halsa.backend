using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Tenancy;

namespace Halsa.Backend.Api.Endpoints.Identidad;

/// <summary>
/// <c>GET /identidad/yo</c> — Devuelve el contexto de tenant e identidad del actor autenticado.
///
/// No exige ningún permiso específico: cualquier actor con un token válido puede consultarlo.
/// Es útil para diagnóstico y para que el frontend muestre los datos de sesión antes de que
/// exista un endpoint ATEL real (SPEC 02+).
/// </summary>
internal sealed class Yo : IEndpoint
{
    internal sealed record Response(
        string Sub,
        string ActorTipo,
        string? TenantId,
        string? IpsId,
        IReadOnlyCollection<string> Permisos);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("identidad/yo", (
                ITenantContext tenantContext,
                ICurrentUser currentUser) =>
            {
                var response = new Response(
                    Sub: tenantContext.Sub,
                    ActorTipo: tenantContext.Tipo.ToString().ToLowerInvariant(),
                    TenantId: tenantContext.TenantId?.Value,
                    IpsId: tenantContext.IpsId?.Value,
                    Permisos: currentUser.Permissions);

                return Results.Ok(response);
            })
        .WithTags("Identidad")
        .WithName("IdentidadYo")
        .WithSummary("Devuelve el contexto de tenant e identidad del actor autenticado.")
        .Produces<Response>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireAuthorization();
}
