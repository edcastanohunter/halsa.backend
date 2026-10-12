using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Casos.Queries.ListCasos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

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

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(
                        title: result.Error.Code,
                        detail: result.Error.Description,
                        statusCode: StatusCodes.Status500InternalServerError);
            })
        .WithTags(Tags.Casos)
        .WithName("ListCasos")
        .WithSummary("Lista paginada de casos visibles para el actor autenticado.")
        .Produces<PagedList<CasoResumen>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosRead);

    internal sealed record ListCasosRequest(
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize,
        [property: FromQuery(Name = "estado")] EstadoCaso? Estado);
}
