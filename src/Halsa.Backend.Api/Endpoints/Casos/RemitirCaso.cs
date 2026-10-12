using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Casos.Commands.Remitir;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>POST /v1/casos/{casoId}/remisiones</c> — Remite el caso a una IPS.
///
/// Devuelve 201 con el id de la nueva remisión, 409 si ya hay una remisión vigente
/// a la misma IPS y 403 si el actor no es una ARL.
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
                var result = await sender.Send(
                    new RemitirCasoCommand(new CasoMedicoId(casoId), request.IpsId), ct);

                return result.IsSuccess
                    ? Results.Created(
                        $"/v1/casos/{casoId}/remisiones/{result.Value}",
                        new { remisionId = result.Value })
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("RemitirCaso")
        .WithSummary("Remite el caso a una IPS.")
        .Produces<object>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite)
        .Auditable(RecursoAuditoria.Caso);
}
