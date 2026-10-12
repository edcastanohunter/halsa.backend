using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Casos.Queries;
using Halsa.Backend.Application.Casos.Queries.ListCasos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>GET /v1/casos</c> — Lista paginada de casos visibles para el actor autenticado.
///
/// La visibilidad real la garantizan las políticas RLS de PostgreSQL y el query filter de EF Core.
/// El endpoint sólo pagina y filtra por estado opcional.
/// </summary>
internal sealed class ListCasos : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos", async (
                [AsParameters] ListCasosRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var query = new ListCasosQuery(
                    request.Page ?? 1,
                    request.PageSize ?? 20,
                    request.Estado);

                var result = await sender.Send(query, ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("ListCasos")
        .WithSummary("Lista paginada de casos visibles para el actor autenticado.")
        .Produces<PagedList<CasoResumenResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosRead)
        .Auditable(RecursoAuditoria.Caso);

    internal sealed record ListCasosRequest(
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize,
        [property: FromQuery(Name = "estado")] EstadoCaso? Estado);
}
