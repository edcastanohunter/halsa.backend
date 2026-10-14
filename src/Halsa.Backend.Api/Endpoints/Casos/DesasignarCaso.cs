using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Casos.Commands.Desasignar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>DELETE /v1/casos/{casoId}/asignaciones/{sub}</c> — Quita un usuario del equipo del caso.
/// Una IPS solo quita las asignaciones que hizo ella misma (SPEC 08).
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
                var result = await sender.Send(
                    new DesasignarCasoCommand(new CasoMedicoId(casoId), sub), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("DesasignarCaso")
        .WithSummary("Elimina la asignación de un usuario del caso.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosAsignar)
        .Auditable(RecursoAuditoria.Caso);
}
