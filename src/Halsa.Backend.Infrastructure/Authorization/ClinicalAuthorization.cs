using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// El helper de los endpoints clínicos (SPEC 08): evalúa una política clínica con el caso como recurso y audita el
/// rechazo como <c>DENEGADO</c> con <c>caso_medico_id</c>. El permiso y el step-up ya los evaluó el middleware
/// (<see cref="Policies.PreviaClinica"/>), así que lo que falla aquí es el ABAC.
/// </summary>
public interface IClinicalAuthorization
{
    /// <returns>
    /// Éxito, o el error de la respuesta: <see cref="ClinicoErrors.CasoNoEncontrado"/> (<c>404</c>),
    /// <see cref="ClinicoErrors.NoAsignado"/> o <see cref="ClinicoErrors.AccesoNoPermitido"/> (<c>403</c>).
    /// </returns>
    /// <param name="accion">La acción de la fila <c>DENEGADO</c>.</param>
    /// <param name="recurso">El recurso de la fila <c>DENEGADO</c>: el registro clínico o el soporte.</param>
    Task<Result> AutorizarAsync(
        ClaimsPrincipal user,
        CasoMedicoId casoId,
        string politica,
        AccionAuditoria accion,
        RecursoAuditoria recurso,
        CancellationToken ct);
}

internal sealed class ClinicalAuthorization(IAuthorizationService authorization, IAuditWriter auditWriter)
    : IClinicalAuthorization
{
    /// <summary>Lo que falla sin un <see cref="AccesoClinicoDenegado"/>: el permiso o el step-up, si el middleware no los evaluó.</summary>
    private static readonly Error OtroRechazo =
        Error.Forbidden("Auth.Denegado", "La política clínica rechazó el acceso.");

    public async Task<Result> AutorizarAsync(
        ClaimsPrincipal user,
        CasoMedicoId casoId,
        string politica,
        AccionAuditoria accion,
        RecursoAuditoria recurso,
        CancellationToken ct)
    {
        AuthorizationResult result = await authorization.AuthorizeAsync(user, casoId, politica);
        if (result.Succeeded)
        {
            return Result.Success();
        }

        AccesoClinicoDenegado? denegado = result.Failure?.FailureReasons.OfType<AccesoClinicoDenegado>().FirstOrDefault();

        // Fails closed: if the denial cannot be audited, the request fails too (still without data).
        await auditWriter.RegistrarAsync(
            accion,
            new ObjetivoAuditoria(recurso, casoId.Value.ToString(), casoId.Value),
            denegado?.TenantDelCaso,
            ResultadoAuditoria.Denegado,
            ct);

        return Result.Failure(denegado?.Error ?? OtroRechazo);
    }
}
