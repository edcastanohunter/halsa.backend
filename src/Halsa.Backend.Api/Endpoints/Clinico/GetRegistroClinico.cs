using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Clinico.Queries.GetRegistroClinico;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>GET /v1/casos/{casoId}/registros-clinicos/{registroId}</c> — Un registro de la historia clínica, descifrado
/// (SPEC 08). La misma política que la lista; un registro <c>DICTAMEN_PCL</c> responde <c>404</c> por esta ruta.
/// </summary>
internal sealed class GetRegistroClinico : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/registros-clinicos/{registroId:guid}", async (
                Guid casoId,
                Guid registroId,
                ClaimsPrincipal user,
                IClinicalAuthorization clinicalAuthorization,
                ISender sender,
                CancellationToken ct) =>
            {
                var caso = new CasoMedicoId(casoId);

                Result acceso = await clinicalAuthorization.AutorizarAsync(
                    user, caso, Policies.ClinicoLectura, AccionAuditoria.Leer, RecursoAuditoria.RegistroClinico, ct);
                if (acceso.IsFailure)
                {
                    return CustomResults.Problem(acceso);
                }

                Result<RegistroClinicoResponse> result = await sender.Send(
                    new GetRegistroClinicoQuery(caso, new HistoriaClinicaDetalleId(registroId)), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Clinico)
        .WithName("GetRegistroClinico")
        .WithSummary("Un registro de la historia clínica del caso (acceso auditado).")
        .Produces<RegistroClinicoResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClinicalPolicy(Policies.ClinicoLectura)
        .Auditable(RecursoAuditoria.RegistroClinico);
}
