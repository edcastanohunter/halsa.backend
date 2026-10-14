using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>GET /v1/casos/{casoId}/registros-clinicos</c> — La historia clínica del caso, descifrada y en orden cronológico
/// (SPEC 08), sin los registros <c>DICTAMEN_PCL</c>.
///
/// Política <see cref="Policies.ClinicoLectura"/>: <c>clinico:read</c> y <c>acr=2</c> en la ruta (<c>403</c> o
/// <c>401</c> con step-up), y luego el ABAC sobre el caso: <c>404</c> si no es visible, <c>403</c> con
/// <c>Clinico.NoAsignado</c> si el profesional no está asignado. Cada lectura deja <c>LEER / REGISTRO_CLINICO</c>.
/// </summary>
internal sealed class GetRegistrosClinicos : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/registros-clinicos", async (
                Guid casoId,
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

                Result<IReadOnlyList<RegistroClinicoResponse>> result =
                    await sender.Send(new GetRegistrosClinicosQuery(caso), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Clinico)
        .WithName("GetRegistrosClinicos")
        .WithSummary("Historia clínica del caso, sin los dictámenes PCL (acceso auditado).")
        .Produces<IReadOnlyList<RegistroClinicoResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClinicalPolicy(Policies.ClinicoLectura)
        .Auditable(RecursoAuditoria.RegistroClinico);
}
