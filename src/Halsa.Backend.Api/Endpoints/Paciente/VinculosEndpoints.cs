using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Pacientes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Paciente;

/// <summary>
/// <c>POST /v1/casos/{casoId}/codigos-activacion</c> — La ARL emite un código de activación del portal para el trabajador
/// del caso (SPEC 12): <c>201 { codigo, expiraEn }</c>, con el código completo una sola vez. <c>404</c> si el caso no es
/// del tenant; <c>409</c> si no está <c>Validado</c> o en un estado posterior.
/// </summary>
internal sealed class EmitirCodigoActivacion : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/codigos-activacion", async (Guid casoId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new EmitirCodigoActivacionCommand(new CasoMedicoId(casoId)), ct);

                return result.IsSuccess
                    ? Results.Created($"/v1/casos/{casoId}/codigos-activacion", result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Casos)
        .WithName("EmitirCodigoActivacion")
        .WithSummary("Emite un código de activación del portal del paciente para el trabajador del caso.")
        .Produces<CodigoActivacionResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.PacientesActivar)
        .Auditable(RecursoAuditoria.CodigoActivacion);
}

/// <summary>
/// <c>POST /v1/paciente/vinculos</c> — <c>{ codigo }</c>. El paciente canjea su código de activación (SPEC 12):
/// <c>201 { trabajador: { tipo, numeroEnmascarado } }</c>. <c>400</c> <c>Paciente.CodigoInvalido</c> (el mismo para un
/// código inexistente, vencido o usado), <c>423</c> <c>Paciente.CodigoBloqueado</c>, <c>429</c>
/// <c>Paciente.DemasiadosIntentos</c> y <c>409</c> <c>Paciente.VinculoDistinto</c>.
/// </summary>
internal sealed class CanjearCodigoActivacion : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("paciente/vinculos", async (CanjearCodigoRequest request, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new CanjearCodigoActivacionCommand(request), ct);

                return result.IsSuccess
                    ? Results.Created("/v1/paciente/vinculos", result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Paciente)
        .WithName("CanjearCodigoActivacion")
        .WithSummary("Vincula la cuenta del paciente al documento del trabajador con un código de activación.")
        .Produces<VinculoCreadoResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status423Locked)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .RequirePaciente()
        .Auditable(RecursoAuditoria.Vinculo);
}
