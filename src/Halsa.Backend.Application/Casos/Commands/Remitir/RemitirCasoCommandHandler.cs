using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Casos.Commands.Remitir;

internal sealed class RemitirCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    TimeProvider time)
    : ICommandHandler<RemitirCasoCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        RemitirCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.PuedeEscribir())
        {
            return Result.Failure<Guid>(CasoMedicoErrors.ActorNoAutorizado);
        }

        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<Guid>(CasoMedicoErrors.NoEncontrado);
        }

        // El validador ya exige 12 dígitos; esto solo construye el value object.
        if (!IpsId.TryCreate(command.IpsId, out IpsId ipsId))
        {
            return Result.Failure<Guid>(CasoMedicoErrors.IpsIdInvalido);
        }

        Result<RemisionId> result = caso.Remitir(ipsId, tenantContext.Sub, time.GetUtcNow());
        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        Result guardado = await unitOfWork.GuardarCasoAsync(cancellationToken);
        return guardado.IsFailure
            ? Result.Failure<Guid>(guardado.Error)
            : Result.Success(result.Value.Value);
    }
}
