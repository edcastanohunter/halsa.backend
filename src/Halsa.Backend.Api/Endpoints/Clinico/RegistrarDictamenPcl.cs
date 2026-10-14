using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>POST /v1/casos/{casoId}/dictamen-pcl</c> — Registra el dictamen PCL del caso, o corrige el vigente con
/// <c>corrigeA</c> mientras no se haya notificado (SPEC 08).
///
/// Política <see cref="Policies.DictamenPclEscritura"/>: <c>pcl:dictamen:write</c>, <c>acr=2</c> de 15 min o menos, y
/// la asignación al caso. El calificador sale del token. Responde <c>201</c>; <c>400</c> si el cuerpo no cumple las
/// reglas; <c>409</c> con <c>Dictamen.YaExiste</c>, <c>Dictamen.YaNotificado</c> o <c>Dictamen.CorrigeANoVigente</c>.
/// </summary>
internal sealed class RegistrarDictamenPcl : IEndpoint
{
    private const string NameClaimType = "name";

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/dictamen-pcl", async (
                Guid casoId,
                RegistrarDictamenPclRequest request,
                ClaimsPrincipal user,
                IClinicalAuthorization clinicalAuthorization,
                IProfesionalActual profesionalActual,
                ISender sender,
                CancellationToken ct) =>
            {
                var caso = new CasoMedicoId(casoId);

                Result acceso = await clinicalAuthorization.AutorizarAsync(
                    user, caso, Policies.DictamenPclEscritura, AccionAuditoria.Crear, RecursoAuditoria.RegistroClinico, ct);
                if (acceso.IsFailure)
                {
                    return CustomResults.Problem(acceso);
                }

                Result<ProfesionalRef> calificador = profesionalActual.Obtener();
                if (calificador.IsFailure)
                {
                    return CustomResults.Problem(calificador);
                }

                Result<DictamenPclResponse> result = await sender.Send(
                    new RegistrarDictamenPclCommand(caso, request, calificador.Value, user.FindFirstValue(NameClaimType)), ct);

                return result.IsSuccess
                    ? Results.Created($"/v1/casos/{casoId}/dictamen-pcl", result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Clinico)
        .WithName("RegistrarDictamenPcl")
        .WithSummary("Registra o corrige el dictamen de pérdida de capacidad laboral del caso.")
        .Produces<DictamenPclResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClinicalPolicy(Policies.DictamenPclEscritura)
        .Auditable(RecursoAuditoria.RegistroClinico);
}
