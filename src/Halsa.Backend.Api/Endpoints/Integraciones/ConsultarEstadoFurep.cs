using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Integraciones;

/// <summary>
/// <c>GET /v1/integraciones/furep/{solicitudId}</c> — Estado del procesamiento asíncrono de un FUREP (enfermedad laboral):
/// <c>RADICADO</c>, <c>VALIDADO</c>, <c>RECHAZADO</c> (con <c>motivoRechazo</c>) o <c>DUPLICADO</c>.
///
/// Mismo permiso y mismo actor que la radicación. Una solicitud de otro tenant responde <c>404</c>, igual que
/// una que no existe.
/// </summary>
internal sealed class ConsultarEstadoFurep : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("integraciones/furep/{solicitudId:guid}", (Guid solicitudId, ISender sender, CancellationToken ct) =>
                IntegracionEndpoints.ConsultarAsync(TipoReporte.Furep, solicitudId, sender, ct))
        .WithTags(Tags.Integraciones)
        .WithName("ConsultarEstadoFurep")
        .WithSummary("Estado del procesamiento asíncrono de un FUREP (enfermedad laboral).")
        .Produces<RadicacionResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.IntegracionFurepWrite)
        .Auditable(RecursoAuditoria.Caso);
}
