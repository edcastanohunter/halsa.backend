using Halsa.Backend.Application.Casos.Commands.RevocarRemision;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>DELETE /v1/casos/{casoId}/remisiones/{remisionId}</c> — Revoca una remisión vigente.
///
/// Devuelve 204 No Content si tiene éxito.
/// </summary>
internal sealed class RevocarRemision : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapDelete("casos/{casoId:guid}/remisiones/{remisionId:guid}", async (
                Guid casoId,
                Guid remisionId,
                ISender sender,
                CancellationToken ct) =>
            {
                var command = new RevocarRemisionCommand(
                    new CasoMedicoId(casoId),
                    new RemisionId(remisionId));

                var result = await sender.Send(command, ct);

                return result.Match(
                    () => Results.NoContent(),
                    error => error.Type switch
                    {
                        ErrorType.NotFound => Results.NotFound(),
                        ErrorType.Conflict => Results.Conflict(new { error.Code, error.Description }),
                        _ => Results.Problem(error.Description)
                    });
            })
        .WithTags(Tags.Casos)
        .WithName("RevocarRemision")
        .WithSummary("Revoca una remisión vigente.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}
