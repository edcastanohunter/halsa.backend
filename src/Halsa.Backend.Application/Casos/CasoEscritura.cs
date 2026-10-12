using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Casos;

/// <summary>
/// Reglas comunes a todos los comandos que escriben un <see cref="CasoMedico"/>.
/// </summary>
internal static class CasoEscritura
{
    /// <summary>
    /// Solo un actor <see cref="ActorTipo.Arl"/> escribe casos, aunque otro actor tenga <c>casos:write</c>.
    /// </summary>
    public static bool PuedeEscribir(this ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Arl;

    /// <summary>
    /// Guarda los cambios y traduce un choque de <c>xmin</c> a <see cref="CasoMedicoErrors.ConflictoConcurrencia"/>.
    /// </summary>
    public static async Task<Result> GuardarCasoAsync(this IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(CasoMedicoErrors.ConflictoConcurrencia);
        }
    }
}
