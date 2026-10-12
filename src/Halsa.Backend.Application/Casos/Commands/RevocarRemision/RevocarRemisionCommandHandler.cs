using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.RevocarRemision;

internal sealed class RevocarRemisionCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    TimeProvider time)
    : ICommandHandler<RevocarRemisionCommand>
{
    public async Task<Result> Handle(
        RevocarRemisionCommand command,
        CancellationToken cancellationToken)
    {
        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);

        if (caso is null)
        {
            return Result.Failure(CasoMedicoErrors.NoEncontrado);
        }

        DateTimeOffset ahora = time.GetUtcNow();
        string revocadoPor = tenantContext.Resuelto ? tenantContext.Sub : "sistema";

        var result = caso.RevocarRemision(command.RemisionId, revocadoPor, ahora);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
