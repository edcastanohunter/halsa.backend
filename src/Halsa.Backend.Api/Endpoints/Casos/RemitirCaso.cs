using Halsa.Backend.Application.Casos.Commands.Remitir;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>POST /v1/casos/{casoId}/remisiones</c> — Remite el caso a una IPS.
///
/// Devuelve 201 Created con el ID de la nueva remisión.
/// Retorna 409 si ya existe una remisión vigente a la misma IPS.
/// </summary>
internal sealed class RemitirCaso : IEndpoint
{
    internal sealed record Request(string IpsId);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/remisiones", async (
                Guid casoId,
                Request request,
                ISender sender,
                CancellationToken ct) =>
            {
                var command = new RemitirCasoCommand(new CasoMedicoId(casoId), request.IpsId);
                var result = await sender.Send(command, ct);

                return result.Match(
                    remisionId => Results.Created(
                        $"/v1/casos/{casoId}/remisiones/{remisionId}",
                        new { id = remisionId }),
                    error => error.Type switch
                    {
                        ErrorType.NotFound => Results.NotFound(),
                        ErrorType.Conflict => Results.Conflict(new { error.Code, error.Description }),
                        ErrorType.Problem => Results.UnprocessableEntity(new { error.Code, error.Description }),
                        _ => Results.Problem(error.Description)
                    });
            })
        .WithTags(Tags.Casos)
        .WithName("RemitirCaso")
        .WithSummary("Remite el caso a una IPS.")
        .Produces<object>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}
