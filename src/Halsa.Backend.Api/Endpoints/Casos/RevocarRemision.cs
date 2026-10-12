using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Casos.Commands.RevocarRemision;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>POST /v1/casos/{casoId}/remisiones/{remisionId}/revocar</c> — Revoca una remisión vigente.
///
/// Las remisiones nunca se borran; revocar marca <c>revocada_en</c>.
/// </summary>
internal sealed class RevocarRemision : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/remisiones/{remisionId:guid}/revocar", async (
                Guid casoId,
                Guid remisionId,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new RevocarRemisionCommand(new CasoMedicoId(casoId), new RemisionId(remisionId)), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("RevocarRemision")
        .WithSummary("Revoca una remisión vigente.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite)
        .Auditable(RecursoAuditoria.Caso, AccionAuditoria.Actualizar);
}
