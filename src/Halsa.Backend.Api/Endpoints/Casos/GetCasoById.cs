using Halsa.Backend.Application.Casos.Queries.GetCasoById;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>GET /v1/casos/{id}</c> — Detalle completo de un caso (remisiones y asignaciones incluidas).
///
/// Devuelve 404 si el caso no existe o no es visible para el tenant/IPS del actor.
/// </summary>
internal sealed class GetCasoById : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{id:guid}", async (
                Guid id,
                ISender sender,
                CancellationToken ct) =>
            {
                var query = new GetCasoByIdQuery(new CasoMedicoId(id));
                var result = await sender.Send(query, ct);

                return result.Match(
                    detalle => Results.Ok(detalle),
                    error => error == CasoMedicoErrors.NoEncontrado
                        ? Results.NotFound()
                        : Results.Problem(error.Description));
            })
        .WithTags(Tags.Casos)
        .WithName("GetCasoById")
        .WithSummary("Detalle completo de un caso médico.")
        .Produces<CasoDetalle>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosRead);
}
