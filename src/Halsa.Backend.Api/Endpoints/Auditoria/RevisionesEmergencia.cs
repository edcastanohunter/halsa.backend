using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Auditoria;

/// <summary>
/// <c>GET /v1/auditoria/revisiones-emergencia?estado=PENDIENTE|CERRADA</c> — Las revisiones de los accesos de emergencia
/// a los casos del tenant (SPEC 08). Solo un actor ARL con <c>auditoria:revisar</c>; la consulta se audita.
/// </summary>
internal sealed class ListRevisionesEmergencia : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("auditoria/revisiones-emergencia", async (
                [AsParameters] Request request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new ListRevisionesEmergenciaQuery(request.Estado, request.Page ?? 1, request.PageSize ?? 50), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Auditoria)
        .WithName("ListRevisionesEmergencia")
        .WithSummary("Revisiones de los accesos de emergencia del tenant (oficial de privacidad).")
        .Produces<PagedList<RevisionEmergenciaResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.AuditoriaRevisar)
        .Auditable(RecursoAuditoria.Auditoria);

    internal sealed record Request(
        [property: FromQuery(Name = "estado")] string? Estado,
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize);
}

/// <summary>
/// <c>POST /v1/auditoria/revisiones-emergencia/{id}/cerrar</c> — <c>{ conclusion, observaciones }</c>. Cierra la
/// revisión (SPEC 08): <c>204</c>; <c>400</c> si la conclusión no es <c>JUSTIFICADO</c> ni <c>INJUSTIFICADO</c>;
/// <c>404</c> si no es del tenant; <c>409</c> si ya estaba cerrada.
/// </summary>
internal sealed class CerrarRevisionEmergencia : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("auditoria/revisiones-emergencia/{id:guid}/cerrar", async (
                Guid id,
                CerrarRevisionEmergenciaRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new CerrarRevisionEmergenciaCommand(id, request.Conclusion, request.Observaciones), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Auditoria)
        .WithName("CerrarRevisionEmergencia")
        .WithSummary("Cierra la revisión de un acceso de emergencia con su conclusión.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.AuditoriaRevisar)
        .Auditable(RecursoAuditoria.Auditoria, AccionAuditoria.Actualizar);
}
