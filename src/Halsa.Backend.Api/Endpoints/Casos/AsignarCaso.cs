using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Casos.Commands.Asignar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>POST /v1/casos/{casoId}/asignaciones</c> — Asigna un usuario al equipo del caso.
///
/// Idempotente: repetir la misma asignación responde 204 y deja una sola fila.
/// </summary>
internal sealed class AsignarCaso : IEndpoint
{
    internal sealed record Request(string Sub);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/asignaciones", async (
                Guid casoId,
                Request request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new AsignarCasoCommand(new CasoMedicoId(casoId), request.Sub), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("AsignarCaso")
        .WithSummary("Asigna un usuario al caso (idempotente).")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}
