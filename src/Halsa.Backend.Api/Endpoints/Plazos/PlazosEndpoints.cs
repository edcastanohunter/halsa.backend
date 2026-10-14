using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Plazos;

/// <summary>
/// <c>GET /v1/casos/{casoId}/plazos</c> — Los plazos legales del caso (SPEC 09): regla, base legal, inicio, vencimiento,
/// estado, si fue extemporáneo y cuándo se cumplió. Solo personal de la ARL; <c>404</c> si el caso no es visible.
/// </summary>
internal sealed class ListPlazosCaso : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/plazos", async (Guid casoId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListPlazosCasoQuery(new CasoMedicoId(casoId)), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Plazos)
        .WithName("ListPlazosCaso")
        .WithSummary("Plazos legales de un caso.")
        .Produces<IReadOnlyList<PlazoResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.PlazosRead)
        .Auditable(RecursoAuditoria.Plazo);
}

/// <summary>
/// <c>GET /v1/plazos?estado=&amp;venceAntesDe=&amp;regla=&amp;page=&amp;pageSize=</c> — Los plazos del tenant (SPEC 09),
/// paginados y ordenados por vencimiento. Solo un actor ARL.
/// </summary>
internal sealed class ListPlazos : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("plazos", async ([AsParameters] Request request, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListPlazosQuery(
                    request.Estado, request.VenceAntesDe, request.Regla, request.Page ?? 1, request.PageSize ?? 50), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Plazos)
        .WithName("ListPlazos")
        .WithSummary("Plazos legales del tenant.")
        .Produces<PagedList<PlazoResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.PlazosRead)
        .Auditable(RecursoAuditoria.Plazo);

    internal sealed record Request(
        [property: FromQuery(Name = "estado")] string? Estado,
        [property: FromQuery(Name = "venceAntesDe")] DateTimeOffset? VenceAntesDe,
        [property: FromQuery(Name = "regla")] string? Regla,
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize);
}

/// <summary>
/// <c>POST /v1/casos/{casoId}/plazos/{plazoId}/cumplir</c> — <c>{ nota, cumplidoEn? }</c>. El cumplimiento manual de un
/// plazo (SPEC 09): <c>204</c>; <c>400</c> si la nota tiene menos de 10 caracteres o la fecha es futura; <c>404</c> si
/// no es del caso o del tenant; <c>409</c> si ya no está <c>ABIERTO</c> ni <c>VENCIDO</c>.
/// </summary>
internal sealed class CumplirPlazo : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/plazos/{plazoId:guid}/cumplir", async (
                Guid casoId,
                Guid plazoId,
                CumplirPlazoRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new CumplirPlazoCommand(new CasoMedicoId(casoId), new PlazoId(plazoId), request), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Plazos)
        .WithName("CumplirPlazo")
        .WithSummary("Marca un plazo como cumplido, con una nota.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.PlazosWrite)
        .Auditable(RecursoAuditoria.Plazo, AccionAuditoria.Actualizar);
}
