using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>POST /v1/casos/{casoId}/dictamen-pcl/notificar</c> — Registra la notificación del dictamen vigente (SPEC 08) y
/// emite <c>DictamenPclNotificadoIntegrationEvent</c>. Política <see cref="Policies.DictamenPclEscritura"/>. Responde
/// <c>204</c>; <c>400</c> si la fecha es futura o anterior al dictamen; <c>409</c> con <c>Dictamen.YaNotificado</c> si ya
/// no está <c>PENDIENTE</c>.
/// </summary>
internal sealed class NotificarDictamenPcl : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/dictamen-pcl/notificar", async (
                Guid casoId,
                NotificarDictamenPclRequest request,
                ClaimsPrincipal user,
                IClinicalAuthorization clinicalAuthorization,
                ISender sender,
                CancellationToken ct) =>
            {
                var caso = new CasoMedicoId(casoId);

                Result acceso = await clinicalAuthorization.AutorizarAsync(
                    user, caso, Policies.DictamenPclEscritura, AccionAuditoria.Actualizar, RecursoAuditoria.Caso, ct);
                if (acceso.IsFailure)
                {
                    return CustomResults.Problem(acceso);
                }

                Result result = await sender.Send(new NotificarDictamenPclCommand(caso, request), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Clinico)
        .WithName("NotificarDictamenPcl")
        .WithSummary("Registra la notificación del dictamen PCL vigente del caso.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClinicalPolicy(Policies.DictamenPclEscritura)
        .Auditable(RecursoAuditoria.Caso, AccionAuditoria.Actualizar);
}
