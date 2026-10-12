using Halsa.Backend.Application.Casos.Commands.Asignar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>PUT /v1/casos/{casoId}/asignaciones/{sub}</c> — Asigna un usuario al caso (idempotente).
/// </summary>
internal sealed class AsignarCaso : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPut("casos/{casoId:guid}/asignaciones/{sub}", async (
                Guid casoId,
                string sub,
                ISender sender,
                CancellationToken ct) =>
            {
                var command = new AsignarCasoCommand(new CasoMedicoId(casoId), sub);
                var result = await sender.Send(command, ct);

                return result.Match(
                    () => Results.NoContent(),
                    error => error.Type == ErrorType.NotFound
                        ? Results.NotFound()
                        : Results.Problem(error.Description));
            })
        .WithTags(Tags.Casos)
        .WithName("AsignarCaso")
        .WithSummary("Asigna un usuario al caso (idempotente).")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}
