using Halsa.Backend.Application.Casos.Commands.Desasignar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>DELETE /v1/casos/{casoId}/asignaciones/{sub}</c> — Elimina la asignación de un usuario.
/// </summary>
internal sealed class DesasignarCaso : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapDelete("casos/{casoId:guid}/asignaciones/{sub}", async (
                Guid casoId,
                string sub,
                ISender sender,
                CancellationToken ct) =>
            {
                var command = new DesasignarCasoCommand(new CasoMedicoId(casoId), sub);
                var result = await sender.Send(command, ct);

                return result.Match(
                    () => Results.NoContent(),
                    error => error.Type switch
                    {
                        ErrorType.NotFound => Results.NotFound(),
                        _ => Results.Problem(error.Description)
                    });
            })
        .WithTags(Tags.Casos)
        .WithName("DesasignarCaso")
        .WithSummary("Elimina la asignación de un usuario del caso.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}
