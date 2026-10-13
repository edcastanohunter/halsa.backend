using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones.Conceptos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Integraciones;

/// <summary>
/// <c>POST /v1/integraciones/conceptos-medicos</c> — Recibe un concepto médico, una evolución o un concepto de
/// rehabilitación desde el sistema de una IPS (SPEC 06), sobre un caso que tiene remitido.
///
/// Es idempotente por <c>Idempotency-Key</c>, como la radicación, pero síncrono: responde <c>201</c> con
/// <c>Location</c> hacia el registro (que se podrá leer con la SPEC 08). <c>400</c> si falta la llave, el cuerpo no
/// cumple el contrato o la atención es anterior al evento; <c>403</c> si el actor no es la integración de una IPS;
/// <c>404</c> si el caso no existe, no está remitido a la IPS o el registro corregido no es visible; <c>409</c> si la
/// llave ya se usó con otro cuerpo o el caso está rechazado o cerrado.
/// </summary>
internal sealed class RecibirConceptoMedico : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("integraciones/conceptos-medicos", (HttpContext http, ISender sender, CancellationToken ct) =>
                IntegracionEndpoints.RecibirConceptoAsync(http, sender, ct))
        .WithTags(Tags.Integraciones)
        .WithName("RecibirConceptoMedico")
        .WithSummary("Recibe un concepto médico desde el sistema de una IPS.")
        .Accepts<ConceptoMedicoV1>("application/json")
        .Produces<RegistroClinicoCreadoResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.IntegracionClinicoWrite)
        .Auditable(RecursoAuditoria.RegistroClinico);
}
