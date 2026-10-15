using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Pacientes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Paciente;

// SPEC 12: the reads of the patient portal. None takes a worker id: everything is resolved from the token's sub (ARQ
// §4.3), so a staff token answers 403 and an unlinked patient gets empty lists.

/// <summary><c>GET /v1/paciente/mis-casos</c> — Los casos de los documentos vinculados, de cualquier ARL.</summary>
internal sealed class ListMisCasos : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("paciente/mis-casos", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListMisCasosQuery(), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("ListMisCasos")
        .WithSummary("Los casos del paciente.")
        .Produces<IReadOnlyList<CasoPacienteResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.Caso);
}

/// <summary>
/// <c>GET /v1/paciente/resumen</c> — Los casos activos, las incapacidades vigentes y los próximos hitos (el fin de la
/// incapacidad vigente y el vencimiento de la controversia de un dictamen notificado).
/// </summary>
internal sealed class GetResumenPaciente : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("paciente/resumen", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetResumenPacienteQuery(), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("GetResumenPaciente")
        .WithSummary("El resumen del paciente.")
        .Produces<ResumenPacienteResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.Caso);
}

/// <summary>
/// <c>GET /v1/paciente/casos/{casoId}/linea-tiempo</c> — Los hitos administrativos del caso, sin diagnósticos,
/// porcentajes ni fundamentos. <c>404</c> si el caso no es de un documento vinculado.
/// </summary>
internal sealed class GetLineaTiempo : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("paciente/casos/{casoId:guid}/linea-tiempo", async (Guid casoId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetLineaTiempoQuery(new CasoMedicoId(casoId)), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("GetLineaTiempoPaciente")
        .WithSummary("La línea de tiempo de un caso del paciente.")
        .Produces<IReadOnlyList<HitoResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.Caso);
}

/// <summary><c>GET /v1/paciente/incapacidades</c> — Las incapacidades de los casos vinculados, sin diagnóstico.</summary>
internal sealed class ListIncapacidadesPaciente : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("paciente/incapacidades", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListIncapacidadesPacienteQuery(), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("ListIncapacidadesPaciente")
        .WithSummary("Las incapacidades del paciente.")
        .Produces<IReadOnlyList<IncapacidadPacienteResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.Incapacidad);
}
