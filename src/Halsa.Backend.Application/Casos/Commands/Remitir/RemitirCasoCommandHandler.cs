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
        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);

        if (caso is null)
        {
            return Result.Failure<Guid>(CasoMedicoErrors.NoEncontrado);
        }

        if (!IpsId.TryCreate(command.IpsId, out IpsId ipsId))
        {
            return Result.Failure<Guid>(Error.Problem(
                "Caso.IpsIdInvalido",
                $"El código REPS '{command.IpsId}' no es válido (debe tener 12 dígitos)."));
        }

        DateTimeOffset ahora = time.GetUtcNow();
        string remitidoPor = tenantContext.Resuelto ? tenantContext.Sub : "sistema";

        var result = caso.Remitir(ipsId, remitidoPor, ahora);

        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result.Value.Value);
    }
}
