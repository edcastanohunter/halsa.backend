using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Incapacidades;

/// <summary>
/// <c>GET /v1/casos/{casoId}/incapacidades</c> — Las incapacidades de un caso (SPEC 07), ordenadas por fecha de
/// inicio, con las alertas de días acumulados. Solo datos administrativos: ni diagnósticos ni profesional.
///
/// Devuelve 404 si el caso no existe o no es visible para el tenant/IPS del actor.
/// </summary>
internal sealed class ListIncapacidades : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/incapacidades", async (
                Guid casoId,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new ListIncapacidadesQuery(new CasoMedicoId(casoId)), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Incapacidades)
        .WithName("ListIncapacidades")
        .WithSummary("Incapacidades de un caso, con sus alertas.")
        .Produces<IReadOnlyList<IncapacidadResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.IncapacidadesRead)
        .Auditable(RecursoAuditoria.Incapacidad);
}
