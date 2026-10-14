using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Clinico.Queries.GetDictamenPcl;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>GET /v1/casos/{casoId}/dictamen-pcl</c> — El dictamen PCL vigente del caso, con su motivación descifrada
/// (SPEC 08). Política <see cref="Policies.DictamenPclLectura"/>: <c>pcl:dictamen:read</c>, <c>acr=2</c> de 15 min o
/// menos, y la asignación. <c>404</c> con <c>Dictamen.NoEncontrado</c> si el caso aún no tiene dictamen.
/// </summary>
internal sealed class GetDictamenPcl : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/dictamen-pcl", async (
                Guid casoId,
                ClaimsPrincipal user,
                IClinicalAuthorization clinicalAuthorization,
                ISender sender,
                HttpContext context,
                CancellationToken ct) =>
            {
                var caso = new CasoMedicoId(casoId);

                Result acceso = await clinicalAuthorization.AutorizarAsync(
                    user, caso, Policies.DictamenPclLectura, AccionAuditoria.Leer, RecursoAuditoria.RegistroClinico, ct);
                if (acceso.IsFailure)
                {
                    return CustomResults.Problem(acceso);
                }

                Result<DictamenPclResponse> result = await sender.Send(new GetDictamenPclQuery(caso), ct);
                if (result.IsFailure)
                {
                    return CustomResults.Problem(result);
                }

                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(result.Value);
            })
        .WithTags(Tags.Clinico)
        .WithName("ObtenerDictamenPcl")
        .WithSummary("Dictamen de pérdida de capacidad laboral (acceso sensible y auditado).")
        .Produces<DictamenPclResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClinicalPolicy(Policies.DictamenPclLectura)
        .Auditable(RecursoAuditoria.RegistroClinico);
}
