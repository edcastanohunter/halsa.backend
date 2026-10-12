using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Casos.Queries;
using Halsa.Backend.Application.Casos.Queries.GetCasoById;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>GET /v1/casos/{casoId}</c> — Detalle de un caso con sus remisiones vigentes y su equipo asignado.
///
/// Devuelve 404 si el caso no existe o no es visible para el tenant/IPS del actor.
/// </summary>
internal sealed class GetCasoById : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}", async (
                Guid casoId,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new GetCasoByIdQuery(new CasoMedicoId(casoId)), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("GetCasoById")
        .WithSummary("Detalle de un caso médico.")
        .Produces<CasoDetalleResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosRead);
}
