using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Clinico.Commands.Corregir;
using Halsa.Backend.Application.Clinico.Commands.Registrar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>POST /v1/casos/{casoId}/registros-clinicos</c> — Un médico del backoffice registra un concepto médico, una
/// evolución o un concepto de rehabilitación (SPEC 06). Con <c>corrigeA</c>, corrige un registro anterior.
///
/// El profesional que firma sale del token, nunca del cuerpo. Responde <c>201</c> con <c>Location</c> hacia el
/// registro (que se podrá leer con la SPEC 08); <c>400</c> si el cuerpo no cumple el contrato o la atención es
/// anterior al evento; <c>403</c> si el actor no es una ARL ni una IPS, o el token no trae la identidad del
/// profesional; <c>404</c> si el caso o el registro corregido no existen o no son visibles; <c>409</c> si el caso
/// está rechazado o cerrado.
/// </summary>
internal sealed class RegistrarRegistroClinico : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/registros-clinicos", async (
                Guid casoId,
                RegistroClinicoRequest request,
                ITenantContext tenantContext,
                IProfesionalActual profesionalActual,
                IValidator<RegistroClinicoRequest> validator,
                ISender sender,
                CancellationToken ct) =>
            {
                // A médico of the case's ARL or of an IPS; the visibility of the case is the command's.
                if (tenantContext.Tipo is not (ActorTipo.Arl or ActorTipo.Ips))
                {
                    return CustomResults.Problem(Result.Failure(ClinicoErrors.ActorNoAutorizado));
                }

                Result<ProfesionalRef> profesional = profesionalActual.Obtener();
                if (profesional.IsFailure)
                {
                    return CustomResults.Problem(profesional);
                }

                ValidationResult validacion = await validator.ValidateAsync(request, ct);
                if (!validacion.IsValid)
                {
                    return CustomResults.Problem(Result.Failure(new ValidationError(
                        [.. validacion.Errors.Select(fallo => Error.Problem(fallo.ErrorCode, fallo.ErrorMessage))])));
                }

                Result<RegistroClinicoCreadoResponse> result = await sender.Send(
                    Comando(new CasoMedicoId(casoId), request, profesional.Value), ct);

                return result.IsSuccess
                    ? Results.Created(
                        $"/v1/casos/{casoId}/registros-clinicos/{result.Value.RegistroId}",
                        result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Clinico)
        .WithName("RegistrarRegistroClinico")
        .WithSummary("Registra un concepto médico, una evolución o un concepto de rehabilitación en el caso.")
        .Produces<RegistroClinicoCreadoResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.ClinicoWrite)
        .Auditable(RecursoAuditoria.RegistroClinico);

    /// <summary>Solo con el cuerpo ya validado: el tipo es uno de los admitidos y los obligatorios vienen.</summary>
    private static IRequest<Result<RegistroClinicoCreadoResponse>> Comando(
        CasoMedicoId casoId, RegistroClinicoRequest request, ProfesionalRef profesional)
    {
        if (!TiposConcepto.TryParse(request.Tipo, out TipoRegistroClinico tipo))
        {
            throw new InvalidOperationException("El tipo se valida antes de construir el comando.");
        }

        var datos = new DatosProfesional(profesional.TipoDocumento, profesional.NumeroDocumento, profesional.RegistroRethus);
        ContenidoClinico contenido = request.Contenido!.ADominio();
        DateTimeOffset atendidoEn = request.AtendidoEn!.Value;

        return request.CorrigeA is Guid corrigeA
            ? new CorregirRegistroClinicoCommand(new HistoriaClinicaDetalleId(corrigeA), casoId, tipo, atendidoEn, contenido, datos)
            : new RegistrarRegistroClinicoCommand(casoId, tipo, atendidoEn, contenido, datos);
    }
}
