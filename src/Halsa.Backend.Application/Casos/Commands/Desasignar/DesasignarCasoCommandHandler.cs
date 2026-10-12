using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.Desasignar;

internal sealed class DesasignarCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext)
    : ICommandHandler<DesasignarCasoCommand>
{
    public async Task<Result> Handle(
        DesasignarCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.PuedeEscribir())
        {
            return Result.Failure(CasoMedicoErrors.ActorNoAutorizado);
        }

        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure(CasoMedicoErrors.NoEncontrado);
        }

        Result result = caso.Desasignar(command.Sub);
        if (result.IsFailure)
        {
            return result;
        }

        return await unitOfWork.GuardarCasoAsync(cancellationToken);
    }
}
