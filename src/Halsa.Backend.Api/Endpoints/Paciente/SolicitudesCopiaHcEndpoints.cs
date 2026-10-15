using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Pacientes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Paciente;

// SPEC 12: the HC copy requests. The patient creates and lists its own; the ARL's privacy officer works the tenant's inbox.
// The copy itself is delivered through the formal channel (Res. 1995/1999, art. 14), never through the portal.

/// <summary>
/// <c>POST /v1/paciente/casos/{casoId}/solicitudes-copia-hc</c> — <c>{ medioEntrega, direccionOCorreo, observaciones }</c>.
/// <c>201</c>; <c>404</c> si el caso no es de un documento vinculado; <c>409</c> si ya hay una abierta para el caso.
/// </summary>
internal sealed class CrearSolicitudCopiaHc : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("paciente/casos/{casoId:guid}/solicitudes-copia-hc", async (
                Guid casoId,
                CrearSolicitudCopiaHcRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new CrearSolicitudCopiaHcCommand(new CasoMedicoId(casoId), request), ct);

                return result.IsSuccess
                    ? Results.Created("/v1/paciente/solicitudes-copia-hc", result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("CrearSolicitudCopiaHc")
        .WithSummary("Solicita la copia de la historia clínica de un caso.")
        .Produces<SolicitudCopiaHcPacienteResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.SolicitudCopiaHc);
}

/// <summary><c>GET /v1/paciente/solicitudes-copia-hc</c> — Las solicitudes del paciente.</summary>
internal sealed class ListSolicitudesCopiaHcPaciente : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("paciente/solicitudes-copia-hc", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListSolicitudesCopiaHcPacienteQuery(), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("ListSolicitudesCopiaHcPaciente")
        .WithSummary("Las solicitudes de copia de la HC del paciente.")
        .Produces<IReadOnlyList<SolicitudCopiaHcPacienteResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.SolicitudCopiaHc);
}

/// <summary><c>GET /v1/solicitudes-copia-hc?estado=</c> — La bandeja de solicitudes del tenant.</summary>
internal sealed class ListSolicitudesCopiaHc : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("solicitudes-copia-hc", async (
                [FromQuery(Name = "estado")] string? estado,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new ListSolicitudesCopiaHcQuery(estado), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.SolicitudesCopiaHc)
        .WithName("ListSolicitudesCopiaHc")
        .WithSummary("La bandeja de solicitudes de copia de la HC del tenant.")
        .Produces<IReadOnlyList<SolicitudCopiaHcResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.SolicitudesHcGestionar)
        .Auditable(RecursoAuditoria.SolicitudCopiaHc);
}

/// <summary>
/// <c>POST /v1/solicitudes-copia-hc/{id}/tramitar</c> — De <c>RECIBIDA</c> a <c>EN_TRAMITE</c>: <c>204</c>; <c>404</c> si
/// no es del tenant; <c>409</c> desde otro estado.
/// </summary>
internal sealed class TramitarSolicitudCopiaHc : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("solicitudes-copia-hc/{id:guid}/tramitar", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new TramitarSolicitudCopiaHcCommand(new SolicitudCopiaHcId(id)), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.SolicitudesCopiaHc)
        .WithName("TramitarSolicitudCopiaHc")
        .WithSummary("Pasa la solicitud a EN_TRAMITE.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.SolicitudesHcGestionar)
        .Auditable(RecursoAuditoria.SolicitudCopiaHc, AccionAuditoria.Actualizar);
}

/// <summary>
/// <c>POST /v1/solicitudes-copia-hc/{id}/cerrar</c> — <c>{ resultado: ENTREGADA | RECHAZADA, nota }</c>. La cierra y cumple
/// su plazo <c>SOLICITUD_COPIA_HC</c>: <c>204</c>; <c>400</c> con un resultado o una nota inválidos; <c>404</c> si no es
/// del tenant; <c>409</c> si ya estaba cerrada.
/// </summary>
internal sealed class CerrarSolicitudCopiaHc : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("solicitudes-copia-hc/{id:guid}/cerrar", async (
                Guid id,
                CerrarSolicitudCopiaHcRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new CerrarSolicitudCopiaHcCommand(new SolicitudCopiaHcId(id), request), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.SolicitudesCopiaHc)
        .WithName("CerrarSolicitudCopiaHc")
        .WithSummary("Cierra la solicitud como ENTREGADA o RECHAZADA y cumple su plazo.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.SolicitudesHcGestionar)
        .Auditable(RecursoAuditoria.SolicitudCopiaHc, AccionAuditoria.Actualizar);
}
