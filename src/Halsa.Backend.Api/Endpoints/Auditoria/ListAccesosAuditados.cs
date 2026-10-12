using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Auditoria;

/// <summary>
/// <c>GET /v1/auditoria/accesos</c> — Accesos auditados para el oficial de privacidad de una ARL.
///
/// Solo un actor ARL con <c>auditoria:read</c>, y la RLS lo limita a su tenant: incluye los accesos de
/// las IPS a sus casos. <c>desde</c> y <c>hasta</c> son obligatorios, con un rango máximo de 31 días.
/// La consulta queda auditada a su vez (<c>LEER</c> sobre <c>AUDITORIA</c>).
/// </summary>
internal sealed class ListAccesosAuditados : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("auditoria/accesos", async (
                [AsParameters] ListAccesosAuditadosRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                // Missing dates become default and the validator rejects them as required.
                var query = new ListAccesosAuditadosQuery(
                    request.Desde ?? default,
                    request.Hasta ?? default,
                    request.CasoId,
                    request.ActorId,
                    request.Accion,
                    request.Resultado,
                    request.Page ?? 1,
                    request.PageSize ?? 50);

                var result = await sender.Send(query, ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Auditoria)
        .WithName("ListAccesosAuditados")
        .WithSummary("Accesos auditados del tenant del actor (oficial de privacidad).")
        .Produces<PagedList<AccesoAuditadoResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.AuditoriaRead);

    internal sealed record ListAccesosAuditadosRequest(
        [property: FromQuery(Name = "desde")] DateTimeOffset? Desde,
        [property: FromQuery(Name = "hasta")] DateTimeOffset? Hasta,
        [property: FromQuery(Name = "casoId")] Guid? CasoId,
        [property: FromQuery(Name = "actorId")] string? ActorId,
        [property: FromQuery(Name = "accion")] string? Accion,
        [property: FromQuery(Name = "resultado")] string? Resultado,
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize);
}
