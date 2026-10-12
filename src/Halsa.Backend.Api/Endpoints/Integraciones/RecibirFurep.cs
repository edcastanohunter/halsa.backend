using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Integraciones;

/// <summary>
/// <c>POST /v1/integraciones/furep</c> — Recibe un FUREP (enfermedad laboral) desde el core de una ARL (Res. 156/2005).
///
/// Responde <c>202</c> en cuanto el reporte queda radicado, con <c>Location</c> hacia la consulta de estado; la
/// deduplicación y la afiliación son asíncronas. <c>400</c> si falta la <c>Idempotency-Key</c> o el cuerpo no
/// cumple el contrato, <c>403</c> si el actor no es la integración de una ARL, y <c>409</c> si la llave ya se usó
/// con otro cuerpo o el reporte ya se radicó con otra llave.
/// </summary>
internal sealed class RecibirFurep : IEndpoint
{
    private const string Ruta = "integraciones/furep";

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost(Ruta, (HttpContext http, ISender sender, CancellationToken ct) =>
                IntegracionEndpoints.RadicarAsync(TipoReporte.Furep, Ruta, http, sender, ct))
        .WithTags(Tags.Integraciones)
        .WithName("RecibirFurep")
        .WithSummary("Recibe un FUREP (enfermedad laboral) desde el core de una ARL.")
        .Accepts<FurepV1>("application/json")
        .Produces<RadicacionResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.IntegracionFurepWrite)
        .Auditable(RecursoAuditoria.Caso);
}
